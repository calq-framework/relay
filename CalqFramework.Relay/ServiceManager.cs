using CalqFramework.Relay.ArgoCD;
using CalqFramework.Relay.Cloud;
using CalqFramework.Relay.Docker;
using CalqFramework.Relay.GitHub;
using CalqFramework.Relay.Kubernetes;
using CalqFramework.Relay.Kustomize;

namespace CalqFramework.Relay;

/// <summary>
///     Manage services: register, remove, and import.
/// </summary>
public class ServiceManager {
    private readonly RelayManager _relay;
    internal ServiceManager(RelayManager relay) => _relay = relay;

    /// <summary>
    ///     Registers a new service and generates all deployment configuration for it.
    /// </summary>
    public RelayResult Add(string name = "", string path = "", bool blueGreen = false, string expose = "none", string domain = "", int port = 8080, string nodePool = "", int minReplicas = 0, int maxReplicas = 0) {
        if (string.IsNullOrEmpty(name)) {
            name = RelayManager.ResolveServiceName();
        }

        if (string.IsNullOrEmpty(path)) {
            path = $"k8s/{name}";
        }

        PlatformConfig cfg = _relay.LoadConfig();
        if (string.IsNullOrEmpty(cfg.Name)) {
            try {
                cfg.Name = Path.GetFileName(
                    CMD("git rev-parse --show-toplevel")
                        .Trim());
            } catch {
                cfg.Name = name;
            }
        }

        if (cfg.Environments.Count == 0) {
            throw new InvalidOperationException("No environments configured. Run 'calq-relay cluster create' or 'calq-relay environment add' first.");
        }

        string? detectedProject = DockerfileGenerator.FindWebProject(".");
        cfg.Services[name] = new ServiceConfig {
            Path = path,
            Project = detectedProject ?? "",
            BlueGreen = blueGreen,
            NodePool = nodePool,
            MinReplicas = minReplicas,
            MaxReplicas = maxReplicas
        };
        _relay.SaveConfig(cfg);
        Console.Error.WriteLine($"Updated {_relay.Config}");

        string clusterProvider = cfg.Environments.Values.First()
            .Clusters.Values.First()
            .Provider;
        KustomizeScaffolder.Scaffold(Path.GetDirectoryName(path) ?? ".", Path.GetFileName(path), blueGreen, expose, domain, clusterProvider, port);
        Console.Error.WriteLine($"Scaffolded at {path}/");

        string argocdDir = Path.Combine(PWD, ".relay", "apps");
        Directory.CreateDirectory(argocdDir);
        string gitRepo = RelayManager.ResolveGitRepo();
        string gitBranch = RelayManager.ResolveGitBranch();
        string destServer = "https://kubernetes.default.svc";
        string destNamespace = RelayManager.GetNamespace(cfg, cfg.Environments.Keys.First());

        if (blueGreen) {
            File.WriteAllText(Path.Combine(argocdDir, $"{name}-blue.yaml"), ApplicationGenerator.Generate($"{name}-blue", gitRepo, $"{path}/blue", gitBranch, destServer, destNamespace, ns: cfg.ArgoCD.Namespace, ignoreSelectorDiff: true));
            File.WriteAllText(Path.Combine(argocdDir, $"{name}-green.yaml"), ApplicationGenerator.Generate($"{name}-green", gitRepo, $"{path}/green", gitBranch, destServer, destNamespace, ns: cfg.ArgoCD.Namespace, ignoreSelectorDiff: true));
        } else {
            File.WriteAllText(Path.Combine(argocdDir, $"{name}.yaml"), ApplicationGenerator.Generate(name, gitRepo, path, gitBranch, destServer, destNamespace, ns: cfg.ArgoCD.Namespace));
        }

        Console.Error.WriteLine("Generated ArgoCD Application(s) in .relay/apps/");

        string workflowsDir = Path.Combine(PWD, ".github", "workflows");
        WorkflowScaffolder.Scaffold(workflowsDir, cfg, clusterProvider);

        return new RelayResult {
            Service = name,
            Operation = "service add",
            SyncStatus = "scaffolded"
        };
    }

    /// <summary>
    ///     Unregisters a service and removes its deployment configuration.
    /// </summary>
    public RelayResult Remove(string name) {
        PlatformConfig cfg = _relay.LoadConfig();
        cfg.Services.Remove(name);
        _relay.SaveConfig(cfg);

        string argocdDir = Path.Combine(PWD, ".relay", "apps");
        if (Directory.Exists(argocdDir)) {
            foreach (string file in Directory.GetFiles(argocdDir, $"{name}*.yaml")) {
                File.Delete(file);
            }
        }

        return new RelayResult {
            Service = name,
            Operation = "service remove",
            SyncStatus = "removed"
        };
    }

    /// <summary>
    ///     Exports pre-existing Kubernetes resources of a service into local manifests for Calq Relay to manage.
    /// </summary>
    public RelayResult Import(string environment = "prod", string cluster = "") {
        PlatformConfig cfg = _relay.LoadConfig();
        ServiceConfig svc = _relay.GetService(cfg);
        EnvironmentConfig env = cfg.Environments[environment];
        ClusterConfig clusterCfg = string.IsNullOrEmpty(cluster) ? env.Clusters.Values.First() : env.Clusters[cluster];
        RelayManager.AuthenticateCluster(clusterCfg);
        string ns = RelayManager.GetNamespace(cfg, environment);
        KubeOps.SetNamespace(ns);
        string selector = $"app={_relay.ServiceName}";
        string yaml = KubeOps.ExportResources(ns, selector);

        string tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, yaml);
        RUN(
            $"yq eval 'del(.metadata.creationTimestamp, .metadata.resourceVersion, .metadata.uid, .spec.clusterIP, .spec.clusterIPs, .spec.ports[].nodePort, .status, .spec.replicas, .metadata.namespace, .metadata.annotations.\"kubectl.kubernetes.io/last-applied-configuration\")' -i {tmpFile}");
        string cleaned = File.ReadAllText(tmpFile);
        File.Delete(tmpFile);

        string baseDir = Path.Combine(svc.Path, "base");
        Directory.CreateDirectory(baseDir);
        File.WriteAllText(Path.Combine(baseDir, "imported.yaml"), cleaned);
        Console.Error.WriteLine($"Imported to {baseDir}/imported.yaml");
        return new RelayResult {
            Service = _relay.ServiceName,
            Operation = "service import",
            SourceEnvironment = environment,
            SyncStatus = "imported"
        };
    }
}
