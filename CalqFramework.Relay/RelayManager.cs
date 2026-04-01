using CalqFramework.Relay.ArgoCD;
using CalqFramework.Relay.Cloud;
using CalqFramework.Relay.Docker;
using CalqFramework.Relay.Kubernetes;
using CalqFramework.Relay.Kustomize;
using CalqFramework.Relay.Platform;

namespace CalqFramework.Relay;

/// <summary>
///     Multi-cloud Kubernetes deployment relay.
///     Orchestrates the full deployment lifecycle: build, deploy, promote,
///     stage, switchover, canary, and restart across environments and clusters.
///     via ArgoCD and GitOps.
/// </summary>
public class RelayManager {
    public RelayManager() {
        Service = new ServiceManager(this);
        Environment = new EnvironmentManager(this);
        Cluster = new ClusterManager(this);
        Config = new ConfigManager(this);
    }
    // ── Global Options ──

    /// <summary>
    ///     Path to the platform configuration file.
    /// </summary>
    [CliName("config")]
    public string ConfigPath { get; set; } = ".relay/relay.json";

    /// <summary>
    ///     Target service name. Auto-detected from .NET project file if empty.
    /// </summary>
    [CliName("service")]
    public string ServiceName { get; set; } = "";

    // ── Submodules ──

    /// <summary>
    ///     Manage services: register, remove, and import.
    /// </summary>
    public ServiceManager Service { get; }

    /// <summary>
    ///     Manage environments: clone and tear down.
    /// </summary>
    public EnvironmentManager Environment { get; }

    /// <summary>
    ///     Manage clusters: provision, install tools, and destroy.
    /// </summary>
    public ClusterManager Cluster { get; }

    /// <summary>
    ///     Manage configuration: sync with organization repo.
    /// </summary>
    public ConfigManager Config { get; }

    // ── Helpers ──

    internal PlatformConfig LoadConfig() {
        if (File.Exists(ConfigPath)) {
            return ConfigLoader.Load(ConfigPath);
        }

        return new PlatformConfig();
    }

    internal void SaveConfig(PlatformConfig cfg) {
        string? dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir)) {
            Directory.CreateDirectory(dir);
        }

        var options = new System.Text.Json.JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = null,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver {
                Modifiers = {
                    IgnoreEmptyStrings
                }
            }
        };
        File.WriteAllText(ConfigPath, System.Text.Json.JsonSerializer.Serialize(cfg, options));
    }

    private static void IgnoreEmptyStrings(System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo) {
        foreach (JsonPropertyInfo prop in typeInfo.Properties) {
            if (prop.PropertyType == typeof(string)) {
                Func<object, object?, bool>? original = prop.ShouldSerialize;
                prop.ShouldSerialize = (obj, val) => val is string s && s.Length > 0 && (original == null || original(obj, val));
            }
        }
    }

    internal ServiceConfig GetService(PlatformConfig cfg) {
        if (string.IsNullOrEmpty(ServiceName)) {
            ServiceName = ResolveServiceName();
        }

        if (!cfg.Services.TryGetValue(ServiceName, out ServiceConfig? svc)) {
            throw new InvalidOperationException($"Service '{ServiceName}' not found in config.");
        }

        return svc;
    }

    internal static string ResolveServiceName() {
        string? proj = DockerfileGenerator.FindWebProject(".") ?? throw new InvalidOperationException("--service is required (no .NET project found to auto-detect). For non-.NET projects, always specify --service.");
        string name = Path.GetFileNameWithoutExtension(proj);
        int lastDot = name.LastIndexOf('.');
        if (lastDot >= 0) {
            name = name[(lastDot + 1)..];
        }

        return ToKebabCase(name);
    }

    private static string ToKebabCase(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2")
            .Replace('.', '-')
            .ToLowerInvariant();

    internal static string ResolveGitRepo() {
        try {
            return CMD("git remote get-url origin")
                .Trim();
        } catch {
            return "";
        }
    }

    internal static string ResolveGitBranch() {
        try {
            return CMD("git symbolic-ref refs/remotes/origin/HEAD")
                .Trim()
                .Replace("refs/remotes/origin/", "");
        } catch {
            return "main";
        }
    }

    internal static string GetNamespace(PlatformConfig cfg, string environment) => $"{cfg.Name}-{environment}";
    private string GetAppName(ServiceConfig svc, string slot = "live") => svc.BlueGreen ? $"{ServiceName}-{slot}" : ServiceName;

    /// <summary>
    ///     Reads the current active slot (blue or green) from the Service's selector.
    /// </summary>
    private string GetActiveSlot(string ns) {
        try {
            return CMD($"kubectl get service {ServiceName} -n {ns} -o jsonpath=\"{{.spec.selector.slot}}\"")
                .Trim();
        } catch {
            return "blue";
        }
    }

    private static string GetInactiveSlot(string activeSlot) => activeSlot == "blue" ? "green" : "blue";

    /// <summary>
    ///     Returns the clusters to operate on. If clusterFilter is specified, returns only that one.
    /// </summary>
    private static Dictionary<string, ClusterConfig> GetClusters(EnvironmentConfig env, string clusterFilter = "") {
        if (!string.IsNullOrEmpty(clusterFilter)) {
            if (!env.Clusters.TryGetValue(clusterFilter, out ClusterConfig? c)) {
                throw new InvalidOperationException($"Cluster '{clusterFilter}' not found in environment.");
            }

            return new() {
                [clusterFilter] = c
            };
        }

        return env.Clusters;
    }

    internal static void AuthenticateCluster(ClusterConfig cluster) => CloudFactory.CreateAuthenticator(cluster.Provider)
        .Authenticate(cluster);

    private static string RewriteImageUrl(string sourceImageUrl, IContainerRegistry tgtRegistry) {
        string imageRef = sourceImageUrl.Contains('/') ? sourceImageUrl[(sourceImageUrl.IndexOf('/') + 1)..] : sourceImageUrl;
        return $"{tgtRegistry.LoginServer}/{imageRef}";
    }

    /// <summary>
    ///     Runs an action on each cluster in the environment (or a single filtered cluster).
    /// </summary>
    private static void ForEachCluster(EnvironmentConfig env, string clusterFilter, Action<string, ClusterConfig> action) {
        foreach (KeyValuePair<string, ClusterConfig> kvp in GetClusters(env, clusterFilter)) {
            Console.Error.WriteLine($"[cluster: {kvp.Key}]");
            AuthenticateCluster(kvp.Value);
            action(kvp.Key, kvp.Value);
        }
    }

    // ── Subcommands ──

    /// <summary>
    ///     Regenerates relay-managed Kustomize patches based on relay.json (node pools, scaling, anti-affinity). Re-runnable.
    /// </summary>
    public RelayResult Scaffold() {
        PlatformConfig cfg = LoadConfig();

        foreach (KeyValuePair<string, ServiceConfig> kvp in cfg.Services) {
            string svcName = kvp.Key;
            ServiceConfig svc = kvp.Value;
            string svcPath = svc.Path;

            string relayDir = svc.BlueGreen ? Path.Combine(svcPath, "blue", "relay") : Path.Combine(svcPath, "relay");

            // Resolve scaling mode and min/max
            NodePoolConfig? pool = null;
            ScalingMode scaling = ScalingMode.None;
            if (!string.IsNullOrEmpty(svc.NodePool) && cfg.NodePools.TryGetValue(svc.NodePool, out pool)) {
                scaling = pool.Scaling;
            }

            // Resolve effective min/max: service overrides pool
            int minReplicas = svc.MinReplicas > 0 ? svc.MinReplicas : pool?.MinNodes ?? 1;
            int maxReplicas = svc.MaxReplicas > 0 ? svc.MaxReplicas : pool?.MaxNodes ?? 10;
            int targetUtil = pool?.TargetUtilization ?? 70;

            // Warn if adaptive maxReplicas exceeds pool maxNodes
            if (scaling == ScalingMode.Adaptive && pool != null && maxReplicas > pool.MaxNodes) {
                Console.Error.WriteLine($"Warning: {svcName} maxReplicas ({maxReplicas}) exceeds pool maxNodes ({pool.MaxNodes})");
            }

            // Clean relay dir first, then regenerate
            if (Directory.Exists(relayDir)) {
                Directory.Delete(relayDir, true);
            }

            if (svc.BlueGreen) {
                string greenRelayDir = Path.Combine(svcPath, "green", "relay");
                if (Directory.Exists(greenRelayDir)) {
                    Directory.Delete(greenRelayDir, true);
                }
            }

            bool needsRelay = false;

            if (scaling == ScalingMode.Grouped) {
                needsRelay = true;
                Directory.CreateDirectory(relayDir);
                File.WriteAllText(Path.Combine(relayDir, "node-selector.yaml"), GenerateNodeSelectorPatch(svcName, svc.NodePool));
                File.WriteAllText(Path.Combine(relayDir, "anti-affinity.yaml"), GenerateAntiAffinityPatch(svcName));
                File.WriteAllText(Path.Combine(relayDir, "scaling-annotation.yaml"), GenerateScalingAnnotationPatch(svcName, "grouped"));
                File.WriteAllText(Path.Combine(relayDir, "hpa.yaml"), GenerateHpa(svcName, svc.BlueGreen, minReplicas, maxReplicas, targetUtilization: targetUtil));

                if (svc.BlueGreen) {
                    string greenRelayDir = Path.Combine(svcPath, "green", "relay");
                    Directory.CreateDirectory(greenRelayDir);
                    File.WriteAllText(Path.Combine(greenRelayDir, "node-selector.yaml"), GenerateNodeSelectorPatch(svcName, svc.NodePool));
                    File.WriteAllText(Path.Combine(greenRelayDir, "anti-affinity.yaml"), GenerateAntiAffinityPatch(svcName));
                    File.WriteAllText(Path.Combine(greenRelayDir, "scaling-annotation.yaml"), GenerateScalingAnnotationPatch($"{svcName}-green", "grouped"));
                    File.WriteAllText(Path.Combine(greenRelayDir, "hpa.yaml"), GenerateHpa(svcName, svc.BlueGreen, minReplicas, maxReplicas, "green", targetUtil));
                }

                Console.Error.WriteLine($"Scaffolded Grouped for {svcName}");
            } else if (scaling == ScalingMode.Adaptive) {
                needsRelay = true;
                Directory.CreateDirectory(relayDir);
                File.WriteAllText(Path.Combine(relayDir, "scaling-annotation.yaml"), GenerateScalingAnnotationPatch(svcName, "adaptive"));
                File.WriteAllText(Path.Combine(relayDir, "anti-affinity.yaml"), GenerateAntiAffinityPatch(svcName));
                File.WriteAllText(Path.Combine(relayDir, "hpa.yaml"), GenerateHpa(svcName, svc.BlueGreen, minReplicas, maxReplicas, targetUtilization: targetUtil));

                if (svc.BlueGreen) {
                    string greenRelayDir = Path.Combine(svcPath, "green", "relay");
                    Directory.CreateDirectory(greenRelayDir);
                    File.WriteAllText(Path.Combine(greenRelayDir, "scaling-annotation.yaml"), GenerateScalingAnnotationPatch($"{svcName}-green", "adaptive"));
                    File.WriteAllText(Path.Combine(greenRelayDir, "anti-affinity.yaml"), GenerateAntiAffinityPatch(svcName));
                    File.WriteAllText(Path.Combine(greenRelayDir, "hpa.yaml"), GenerateHpa(svcName, svc.BlueGreen, minReplicas, maxReplicas, "green", targetUtil));
                }

                Console.Error.WriteLine($"Scaffolded Adaptive for {svcName}");
            } else if (svc.MinReplicas > 0 || svc.MaxReplicas > 0) {
                // Scaling: None but service has min/max  Escaffold HPA only
                needsRelay = true;
                Directory.CreateDirectory(relayDir);
                File.WriteAllText(Path.Combine(relayDir, "hpa.yaml"), GenerateHpa(svcName, svc.BlueGreen, minReplicas, maxReplicas, targetUtilization: targetUtil));

                if (svc.BlueGreen) {
                    string greenRelayDir = Path.Combine(svcPath, "green", "relay");
                    Directory.CreateDirectory(greenRelayDir);
                    File.WriteAllText(Path.Combine(greenRelayDir, "hpa.yaml"), GenerateHpa(svcName, svc.BlueGreen, minReplicas, maxReplicas, "green", targetUtil));
                }

                Console.Error.WriteLine($"Scaffolded HPA for {svcName} ({minReplicas}-{maxReplicas})");
            }

            if (!needsRelay) {
                Console.Error.WriteLine($"No scaling config for {svcName}");
            }
        }

        return new RelayResult {
            Service = "*",
            Operation = "scaffold",
            SyncStatus = "scaffolded"
        };
    }

    private static string GenerateNodeSelectorPatch(string serviceName, string nodePool) =>
        "apiVersion: apps/v1\n" + "kind: Deployment\n" + "metadata:\n" + $"  name: {serviceName}\n" + "spec:\n" + "  template:\n" + "    spec:\n" + "      nodeSelector:\n" + $"        agentpool: {nodePool}\n";

    private static string GenerateHpa(string serviceName, bool blueGreen, int minReplicas, int maxReplicas, string slot = "", int targetUtilization = 70) {
        string deployName = blueGreen && !string.IsNullOrEmpty(slot) ? $"{serviceName}-{slot}" : serviceName;
        return "apiVersion: autoscaling/v2\n" + "kind: HorizontalPodAutoscaler\n" + "metadata:\n" + $"  name: {deployName}\n" + "spec:\n" + "  scaleTargetRef:\n" + "    apiVersion: apps/v1\n" + "    kind: Deployment\n" + $"    name: {deployName}\n" +
               $"  minReplicas: {minReplicas}\n" + $"  maxReplicas: {maxReplicas}\n" + "  metrics:\n" + "  - type: Resource\n" + "    resource:\n" + "      name: cpu\n" + "      target:\n" + "        type: Utilization\n" + "        averageUtilization: " +
               targetUtilization + "\n";
    }

    private static string GenerateAntiAffinityPatch(string serviceName) =>
        "apiVersion: apps/v1\n" + "kind: Deployment\n" + "metadata:\n" + $"  name: {serviceName}\n" + "spec:\n" + "  template:\n" + "    spec:\n" + "      affinity:\n" + "        podAntiAffinity:\n" +
        "          requiredDuringSchedulingIgnoredDuringExecution:\n" + "            - labelSelector:\n" + "                matchExpressions:\n" + "                  - key: app\n" + "                    operator: In\n" + "                    values:\n" +
        $"                      - \"{serviceName}\"\n" + "              topologyKey: kubernetes.io/hostname\n";

    private static string GenerateScalingAnnotationPatch(string deploymentName, string scalingMode) =>
        "apiVersion: apps/v1\n" + "kind: Deployment\n" + "metadata:\n" + $"  name: {deploymentName}\n" + "  annotations:\n" + $"    relay.calq.io/scaling: {scalingMode}\n";

    /// <summary>
    ///     Registers clusters and repos with ArgoCD, generates Application manifests, and syncs. Re-runnable.
    /// </summary>
    public RelayResult Setup(bool dryRun = false) {
        PlatformConfig cfg = LoadConfig();
        string installCluster = !string.IsNullOrEmpty(cfg.ArgoCD.InstallCluster) ? cfg.ArgoCD.InstallCluster : cfg.Environments.Keys.First();
        EnvironmentConfig installEnv = cfg.Environments[installCluster];
        ClusterConfig firstCluster = installEnv.Clusters.Values.First();
        AuthenticateCluster(firstCluster);

        if (!dryRun) {
            ArgoCDInstaller.Login(cfg.ArgoCD.Namespace);

            foreach (EnvironmentConfig env in cfg.Environments.Values)
                foreach (ClusterConfig cluster in env.Clusters.Values) {
                    AuthenticateCluster(cluster);
                    string ctx = CloudFactory.CreateAuthenticator(cluster.Provider)
                        .GetContextName(cluster);
                    ClusterRegistrar.Register(cluster, ctx);
                }

            AuthenticateCluster(firstCluster);
            ArgoCDInstaller.Login(cfg.ArgoCD.Namespace);

            string gitRepo = ResolveGitRepo();
            if (!string.IsNullOrEmpty(gitRepo)) {
                RepoRegistrar.RegisterHttps(
                    gitRepo,
                    "x-access-token",
                    CMD("gh auth token")
                        .Trim());
            }
        }

        string argocdDir = Path.Combine(PWD, ".relay", "apps");
        Directory.CreateDirectory(argocdDir);
        string gitRepoUrl = ResolveGitRepo();
        string gitBranch = ResolveGitBranch();
        string destServer = string.IsNullOrEmpty(firstCluster.ServerUrl) ? "https://kubernetes.default.svc" : firstCluster.ServerUrl;

        foreach (KeyValuePair<string, ServiceConfig> kvp in cfg.Services) {
            string svcName = kvp.Key;
            ServiceConfig svc = kvp.Value;
            string ns = GetNamespace(cfg, installCluster);

            if (svc.BlueGreen) {
                File.WriteAllText(Path.Combine(argocdDir, $"{svcName}-blue.yaml"), ApplicationGenerator.Generate($"{svcName}-blue", gitRepoUrl, $"{svc.Path}/blue", gitBranch, destServer, ns, ns: cfg.ArgoCD.Namespace, ignoreSelectorDiff: true));
                File.WriteAllText(Path.Combine(argocdDir, $"{svcName}-green.yaml"), ApplicationGenerator.Generate($"{svcName}-green", gitRepoUrl, $"{svc.Path}/green", gitBranch, destServer, ns, ns: cfg.ArgoCD.Namespace, ignoreSelectorDiff: true));
            } else {
                File.WriteAllText(Path.Combine(argocdDir, $"{svcName}.yaml"), ApplicationGenerator.Generate(svcName, gitRepoUrl, svc.Path, gitBranch, destServer, ns, ns: cfg.ArgoCD.Namespace));
            }
        }

        File.WriteAllText(Path.Combine(argocdDir, "root.yaml"), ApplicationGenerator.GenerateRootApp(cfg.Name, gitRepoUrl, gitBranch, ".relay/apps", destServer, cfg.ArgoCD.Namespace));

        bool hasAdaptive = cfg.NodePools.Values.Any(p => p.Scaling == ScalingMode.Adaptive);

        if (cfg.ArgoCD.PodRecycling || cfg.ArgoCD.CanaryEnforcement || hasAdaptive) {
            string platformDir = Path.Combine(PWD, ".relay", "platform");
            Directory.CreateDirectory(platformDir);

            // Import kubectl image once  Eall CronJobs use the same image
            EnvironmentConfig platformEnv = cfg.Environments[installCluster];
            IContainerRegistry platformRegistry = CloudFactory.CreateRegistry(platformEnv.Registry);
            string kubectlImage = "bitnami/kubectl:latest";
            if (!dryRun) {
                try {
                    platformRegistry.Authenticate();
                    platformRegistry.ImportImage(kubectlImage);
                    kubectlImage = RewriteImageUrl("kubectl:latest", platformRegistry);
                    Console.Error.WriteLine($"Imported kubectl image to {kubectlImage}");
                } catch {
                    Console.Error.WriteLine("Could not import kubectl image to internal registry, using Docker Hub.");
                }
            }

            if (cfg.ArgoCD.PodRecycling) {
                File.WriteAllText(Path.Combine(platformDir, "pod-recycler.yaml"), PodRecyclerGenerator.Generate(kubectlImage: kubectlImage));
            } else if (File.Exists(Path.Combine(platformDir, "pod-recycler.yaml"))) {
                File.Delete(Path.Combine(platformDir, "pod-recycler.yaml"));
            }

            if (cfg.ArgoCD.CanaryEnforcement) {
                File.WriteAllText(Path.Combine(platformDir, "canary-enforcer.yaml"), CanaryEnforcerGenerator.Generate(kubectlImage: kubectlImage));
            } else if (File.Exists(Path.Combine(platformDir, "canary-enforcer.yaml"))) {
                File.Delete(Path.Combine(platformDir, "canary-enforcer.yaml"));
            }

            if (hasAdaptive) {
                File.WriteAllText(Path.Combine(platformDir, "adaptive-scaler.yaml"), AdaptiveScalerGenerator.Generate(kubectlImage: kubectlImage));
            } else if (File.Exists(Path.Combine(platformDir, "adaptive-scaler.yaml"))) {
                File.Delete(Path.Combine(platformDir, "adaptive-scaler.yaml"));
            }

            File.WriteAllText(Path.Combine(argocdDir, "platform.yaml"), ApplicationGenerator.Generate($"{cfg.Name}-platform", gitRepoUrl, ".relay/platform", gitBranch, destServer, "calq-relay-system", true, cfg.ArgoCD.Namespace));
        } else {
            // Both disabled  Eclean up platform files
            string platformDir = Path.Combine(PWD, ".relay", "platform");
            if (Directory.Exists(platformDir)) {
                Directory.Delete(platformDir, true);
            }

            string platformApp = Path.Combine(argocdDir, "platform.yaml");
            if (File.Exists(platformApp)) {
                File.Delete(platformApp);
            }
        }

        if (!dryRun) {
            RUN($"kubectl apply -f {Path.Combine(argocdDir, "root.yaml")}");
            SyncManager.Sync($"{cfg.Name}-root");
        }

        return new RelayResult {
            Service = "*",
            Operation = "setup",
            SyncStatus = dryRun ? "dry-run" : "synced",
            DryRun = dryRun
        };
    }

    /// <summary>
    ///     Builds, pushes, and deploys the service to the target environment.
    /// </summary>
    public RelayResult Deploy(string environment = "dev", string? project = null, bool dryRun = false) {
        PlatformConfig cfg = LoadConfig();
        ServiceConfig svc = GetService(cfg);
        EnvironmentConfig env = cfg.Environments[environment];
        BuildConfig build = svc.Build;

        // Resolve Dockerfile: explicit path > existing file > auto-generate for .NET
        string dockerfilePath;
        if (!string.IsNullOrEmpty(build.Dockerfile)) {
            dockerfilePath = Path.GetFullPath(build.Dockerfile);
        } else {
            dockerfilePath = Path.GetFullPath("Dockerfile");
            if (!File.Exists(dockerfilePath) && !dryRun) {
                string projectPath = project ?? (string.IsNullOrEmpty(svc.Project) ? null : svc.Project) ??
                    DockerfileGenerator.FindWebProject(".") ?? throw new InvalidOperationException("No Dockerfile found. Provide one or specify --project for .NET auto-generation.");
                Console.Error.WriteLine($"Project: {projectPath}");
                File.WriteAllText(dockerfilePath, DockerfileGenerator.Generate(projectPath));
                Console.Error.WriteLine("Generated Dockerfile");

                string dockerignorePath = Path.GetFullPath(".dockerignore");
                if (!File.Exists(dockerignorePath)) {
                    File.WriteAllText(dockerignorePath, ".git\n.relay\n.github\nbin\nobj\nnode_modules\n");
                    Console.Error.WriteLine("Generated .dockerignore");
                }
            }
        }

        if (!File.Exists(Path.Combine(".", svc.Path, "kustomization.yaml")) && !File.Exists(Path.Combine(".", svc.Path, "base", "kustomization.yaml")) && !dryRun) {
            string provider = env.Clusters.Values.FirstOrDefault()
                ?.Provider ?? "";
            KustomizeScaffolder.Scaffold(PWD, svc.Path, svc.BlueGreen, clusterProvider: provider);
            Console.Error.WriteLine($"Scaffolded at {svc.Path}/");
        }

        IContainerRegistry registry = CloudFactory.CreateRegistry(env.Registry);
        string gitSha = CMD("git rev-parse HEAD")
            .Trim()[..12];
        string tag = build.Tag.Replace("{sha}", gitSha);
        string imageUrl = RewriteImageUrl($"{ServiceName}:{tag}", registry);
        Console.Error.WriteLine($"Image: {imageUrl}");

        if (!dryRun) {
            registry.Authenticate();

            string buildCmd = build.BuildCommand.Replace("{dockerfile}", dockerfilePath)
                .Replace("{image}", imageUrl)
                .Replace("{context}", build.Context);
            RUN(buildCmd);

            string pushCmd = build.PushCommand.Replace("{image}", imageUrl);
            RUN(pushCmd);

            string slot = "live";
            if (svc.BlueGreen) {
                try {
                    ClusterConfig deployCluster = env.Clusters.Values.First();
                    AuthenticateCluster(deployCluster);
                    string ns = GetNamespace(cfg, environment);
                    KubeOps.SetNamespace(ns);
                    slot = GetActiveSlot(ns);
                } catch {
                    slot = "blue";
                }
            }

            string appName = GetAppName(svc, slot);
            try {
                SyncManager.SetImage(appName, ServiceName, imageUrl);
                SyncManager.Sync(appName);
                SyncManager.WaitHealthy(appName);
            } catch {
                Console.Error.WriteLine($"ArgoCD app '{appName}' not found. Run 'calq-relay setup' to register it.");
            }
        }

        return new RelayResult {
            Service = ServiceName,
            Operation = "deploy",
            TargetEnvironment = environment,
            ImageUrl = imageUrl,
            SyncStatus = dryRun ? "dry-run" : "healthy",
            DryRun = dryRun
        };
    }

    /// <summary>
    ///     Copies a deployment from one environment to another (e.g., dev to prod).
    /// </summary>
    public RelayResult Promote(string source = "dev", string target = "prod", bool dryRun = false) {
        PlatformConfig cfg = LoadConfig();
        ServiceConfig svc = GetService(cfg);
        string srcNs = GetNamespace(cfg, source);
        ClusterConfig srcCluster = cfg.Environments[source]
            .Clusters.Values.First();
        AuthenticateCluster(srcCluster);
        KubeOps.SetNamespace(srcNs);
        if (svc.BlueGreen) {
            try {
                string slot = GetActiveSlot(srcNs);
            } catch {
            }
        }

        string sourceImage = SyncManager.GetImage(ServiceName, srcNs);
        Console.Error.WriteLine($"Source image: {sourceImage}");

        IContainerRegistry srcRegistry = CloudFactory.CreateRegistry(cfg.Environments[source].Registry);
        IContainerRegistry tgtRegistry = CloudFactory.CreateRegistry(cfg.Environments[target].Registry);
        if (!dryRun) {
            tgtRegistry.Authenticate();
            tgtRegistry.ImportImage(sourceImage, srcRegistry);
        }

        string targetImage = RewriteImageUrl(sourceImage, tgtRegistry);
        if (!dryRun) {
            string targetSlot = "live";
            if (svc.BlueGreen) {
                try {
                    ClusterConfig tgtCluster = cfg.Environments[target]
                        .Clusters.Values.First();
                    AuthenticateCluster(tgtCluster);
                    string tgtNs = GetNamespace(cfg, target);
                    KubeOps.SetNamespace(tgtNs);
                    targetSlot = GetActiveSlot(tgtNs);
                } catch {
                    targetSlot = "blue";
                }
            }

            string appName = GetAppName(svc, targetSlot);
            SyncManager.SetImage(appName, ServiceName, targetImage);
            SyncManager.Sync(appName);
            SyncManager.WaitHealthy(appName);
        }

        return new RelayResult {
            Service = ServiceName,
            Operation = "promote",
            SourceEnvironment = source,
            TargetEnvironment = target,
            ImageUrl = targetImage,
            SyncStatus = dryRun ? "dry-run" : "healthy",
            DryRun = dryRun
        };
    }

    /// <summary>
    ///     Deploys a new version to the standby slot for verification before switching live traffic.
    /// </summary>
    public RelayResult Stage(string source = "dev", string target = "prod", bool dryRun = false) {
        PlatformConfig cfg = LoadConfig();
        ServiceConfig svc = GetService(cfg);
        if (!svc.BlueGreen) {
            throw new InvalidOperationException($"Service '{ServiceName}' is not configured for blue-green.");
        }

        string srcNs = GetNamespace(cfg, source);
        if (svc.BlueGreen) {
            try {
                ClusterConfig srcCluster = cfg.Environments[source]
                    .Clusters.Values.First();
                AuthenticateCluster(srcCluster);
                KubeOps.SetNamespace(srcNs);
                // Determine active slot on source to read the current image from ArgoCD
                string activeSlotSrc = GetActiveSlot(srcNs);
            } catch {
            }
        }

        // Read source image from ArgoCD
        string sourceImage = SyncManager.GetImage(ServiceName, srcNs);

        // Import image
        IContainerRegistry srcRegistry = CloudFactory.CreateRegistry(cfg.Environments[source].Registry);
        IContainerRegistry tgtRegistry = CloudFactory.CreateRegistry(cfg.Environments[target].Registry);
        if (!dryRun) {
            tgtRegistry.Authenticate();
            tgtRegistry.ImportImage(sourceImage, srcRegistry);
        }

        string targetImage = RewriteImageUrl(sourceImage, tgtRegistry);

        // Determine inactive slot on target
        ClusterConfig tgtCluster = cfg.Environments[target]
            .Clusters.Values.First();
        AuthenticateCluster(tgtCluster);
        string tgtNs = GetNamespace(cfg, target);
        KubeOps.SetNamespace(tgtNs);
        string activeSlot = GetActiveSlot(tgtNs);
        string inactiveSlot = GetInactiveSlot(activeSlot);
        Console.Error.WriteLine($"Active: {activeSlot}, staging to: {inactiveSlot}");

        // Set image override on inactive slot via ArgoCD
        if (!dryRun) {
            SyncManager.SetImage(GetAppName(svc, inactiveSlot), ServiceName, targetImage);
            SyncManager.Sync(GetAppName(svc, inactiveSlot));
            SyncManager.WaitHealthy(GetAppName(svc, inactiveSlot));
        }

        return new RelayResult {
            Service = ServiceName,
            Operation = "stage",
            SourceEnvironment = source,
            TargetEnvironment = target,
            ImageUrl = targetImage,
            SyncStatus = dryRun ? "dry-run" : "healthy",
            DryRun = dryRun
        };
    }


    /// <summary>
    ///     Switches live traffic to the staged version instantly, with no downtime.
    /// </summary>
    /// <param name="environment">Target environment.</param>
    /// <param name="cluster">Optional: target a single cluster instead of all.</param>
    /// <param name="dryRun">Log actions without modifying cluster or Git state.</param>
    public RelayResult Switchover(string environment = "prod", string cluster = "", bool dryRun = false) {
        PlatformConfig cfg = LoadConfig();
        ServiceConfig svc = GetService(cfg);
        if (!svc.BlueGreen) {
            throw new InvalidOperationException($"Service '{ServiceName}' is not configured for blue-green.");
        }

        EnvironmentConfig env = cfg.Environments[environment];
        string ns = GetNamespace(cfg, environment);

        // Determine active/inactive slots from the first cluster
        ClusterConfig firstCluster = GetClusters(env, cluster)
            .Values.First();
        AuthenticateCluster(firstCluster);
        KubeOps.SetNamespace(ns);
        string activeSlot = GetActiveSlot(ns);
        string inactiveSlot = GetInactiveSlot(activeSlot);
        Console.Error.WriteLine($"Active slot: {activeSlot}, switching to: {inactiveSlot}");

        if (!dryRun) {
            // Pre-scale inactive deployment to match active replica count on all clusters
            ForEachCluster(
                env,
                cluster,
                (clusterName, clusterCfg) => {
                    KubeOps.SetNamespace(ns);
                    try {
                        int activeReplicas = KubeOps.GetReplicaCount($"{ServiceName}-{activeSlot}", ns);
                        RUN($"kubectl scale deployment {ServiceName}-{inactiveSlot} -n {ns} --replicas={activeReplicas}");
                        RUN($"kubectl rollout status deployment/{ServiceName}-{inactiveSlot} -n {ns} --timeout=300s");
                        Console.Error.WriteLine($"Scaled {ServiceName}-{inactiveSlot} to {activeReplicas} replicas on {clusterName}.");
                    } catch {
                        Console.Error.WriteLine($"Could not pre-scale on {clusterName}.");
                    }
                });

            // Patch Service selector on all clusters - instant traffic switch
            ForEachCluster(
                env,
                cluster,
                (clusterName, clusterCfg) => {
                    KubeOps.SetNamespace(ns);
                    RUN($"kubectl patch service {ServiceName} -n {ns} -p '{{\"spec\":{{\"selector\":{{\"slot\":\"{inactiveSlot}\"}}}}}}'");

                    // Remove canary annotations if present (ends any active canary)
                    try {
                        RUN($"kubectl annotate service {ServiceName} -n {ns} relay.calq.io/canary-weight- relay.calq.io/active-slot-");
                    } catch {
                    }

                    Console.Error.WriteLine($"Switched {ServiceName} to {inactiveSlot} on {clusterName}.");
                });

            // Tell ArgoCD to ignore the selector field so it doesn't revert the switchover
            SyncManager.IgnoreDiff(GetAppName(svc, "blue"), "/spec/selector");
            SyncManager.IgnoreDiff(GetAppName(svc, "green"), "/spec/selector");
        }

        return new RelayResult {
            Service = ServiceName,
            Operation = "switchover",
            TargetEnvironment = environment,
            ImageUrl = inactiveSlot,
            SyncStatus = dryRun ? "dry-run" : "switched",
            DryRun = dryRun
        };
    }


    /// <summary>
    ///     Sends a percentage of live traffic to the staged version for gradual rollout.
    /// </summary>
    /// <param name="weight">Percentage of traffic for the new version (1-99). Translates to replica ratio.</param>
    /// <param name="environment">Target environment.</param>
    /// <param name="cluster">Optional: target a single cluster.</param>
    public RelayResult Canary(int weight = 10, string environment = "prod", string cluster = "") {
        PlatformConfig cfg = LoadConfig();
        ServiceConfig svc = GetService(cfg);
        if (!svc.BlueGreen) {
            throw new InvalidOperationException($"Service '{ServiceName}' is not configured for blue-green.");
        }

        if (weight < 1 || weight > 99) {
            throw new InvalidOperationException("--weight must be between 1 and 99.");
        }

        EnvironmentConfig env = cfg.Environments[environment];
        string ns = GetNamespace(cfg, environment);

        ClusterConfig firstCluster = GetClusters(env, cluster)
            .Values.First();
        AuthenticateCluster(firstCluster);
        KubeOps.SetNamespace(ns);
        string activeSlot = GetActiveSlot(ns);
        string inactiveSlot = GetInactiveSlot(activeSlot);

        ForEachCluster(
            env,
            cluster,
            (clusterName, clusterCfg) => {
                KubeOps.SetNamespace(ns);

                // Get current active replica count
                int activeReplicas = KubeOps.GetReplicaCount($"{ServiceName}-{activeSlot}", ns);
                int totalReplicas = Math.Max(activeReplicas, 2);

                // Calculate replica split
                int inactiveReplicas = Math.Max(1, (int)Math.Round(totalReplicas * weight / 100.0));
                int newActiveReplicas = totalReplicas - inactiveReplicas;

                // Scale both deployments
                RUN($"kubectl scale deployment {ServiceName}-{inactiveSlot} -n {ns} --replicas={inactiveReplicas}");
                RUN($"kubectl scale deployment {ServiceName}-{activeSlot} -n {ns} --replicas={newActiveReplicas}");
                RUN($"kubectl rollout status deployment/{ServiceName}-{inactiveSlot} -n {ns} --timeout=300s");

                // Widen Service selector to match both slots (use app label only, drop slot)
                RUN($"kubectl patch service {ServiceName} -n {ns} --type json -p '[{{\"op\":\"remove\",\"path\":\"/spec/selector/slot\"}}]'");

                // Set canary annotations so the enforcer CronJob maintains the ratio
                RUN($"kubectl annotate service {ServiceName} -n {ns} relay.calq.io/canary-weight=\"{weight}\" --overwrite");
                RUN($"kubectl annotate service {ServiceName} -n {ns} relay.calq.io/active-slot=\"{activeSlot}\" --overwrite");

                Console.Error.WriteLine($"Canary: {newActiveReplicas} replicas ({activeSlot}) + {inactiveReplicas} replicas ({inactiveSlot}) = ~{weight}% new on {clusterName}");
            });

        return new RelayResult {
            Service = ServiceName,
            Operation = "canary",
            TargetEnvironment = environment,
            ImageUrl = $"{weight}%",
            SyncStatus = "canary"
        };
    }

    /// <summary>
    ///     Restarts the service across all clusters in the environment.
    /// </summary>
    /// <param name="environment">Target environment.</param>
    /// <param name="cluster">Optional: target a single cluster.</param>
    /// <param name="sequential">Use sequential rollout restart instead of parallel anti-affinity.</param>
    public RelayResult Restart(string environment = "prod", string cluster = "", bool sequential = false) {
        PlatformConfig cfg = LoadConfig();
        ServiceConfig svc = GetService(cfg);
        EnvironmentConfig env = cfg.Environments[environment];
        string ns = GetNamespace(cfg, environment);

        ForEachCluster(
            env,
            cluster,
            (clusterName, clusterCfg) => {
                KubeOps.SetNamespace(ns);
                string selector = svc.BlueGreen ? $"app={ServiceName},slot={GetActiveSlot(ns)}" : $"app={ServiceName}";
                string deployName = KubeOps.GetDeploymentBySelector(selector, ns);
                if (sequential) {
                    KubeOps.RestartSequential(deployName, ns);
                } else {
                    KubeOps.RestartParallel(deployName, ns);
                }
            });
        return new RelayResult {
            Service = ServiceName,
            Operation = "restart",
            TargetEnvironment = environment,
            SyncStatus = "restarting"
        };
    }
}
