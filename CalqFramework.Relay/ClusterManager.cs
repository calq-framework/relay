using CalqFramework.Relay.Cloud;

namespace CalqFramework.Relay;

/// <summary>
///     Manage clusters: provision, install tools, and destroy.
/// </summary>
public class ClusterManager {
    private readonly RelayManager _relay;
    internal ClusterManager(RelayManager relay) => _relay = relay;

    /// <summary>
    ///     Registers an existing cluster with an environment.
    /// </summary>
    public RelayResult Add(string cluster, string clusterProvider, string environment = "dev", string registry = "", string registryProvider = "", string resourceGroup = "", string project = "", string region = "") {
        PlatformConfig cfg = _relay.LoadConfig();
        if (string.IsNullOrEmpty(registryProvider)) {
            registryProvider = clusterProvider;
        }

        string clusterCore = cluster;
        if (clusterCore.StartsWith("aks-")) {
            clusterCore = clusterCore["aks-".Length..];
        }

        if (clusterCore.StartsWith("gke-")) {
            clusterCore = clusterCore["gke-".Length..];
        }

        if (clusterProvider.ToLowerInvariant() is "azure" or "aks") {
            if (string.IsNullOrEmpty(resourceGroup)) {
                resourceGroup = $"rg-{clusterCore}";
            }

            if (string.IsNullOrEmpty(registry)) {
                registry = $"acr{clusterCore.Replace("-", "")}";
            }
        } else if (clusterProvider.ToLowerInvariant() is "gcp" or "gke") {
            if (string.IsNullOrEmpty(project)) {
                try {
                    project = CMD("gcloud config get-value project")
                        .Trim();
                } catch {
                    throw new InvalidOperationException("--project is required for GCP.");
                }
            }

            if (string.IsNullOrEmpty(region)) {
                try {
                    region = CMD("gcloud config get-value compute/region")
                        .Trim();
                } catch {
                    region = "us-central1";
                }
            }

            if (string.IsNullOrEmpty(registry)) {
                registry = environment;
            }
        }

        if (!cfg.Environments.TryGetValue(environment, out EnvironmentConfig? env)) {
            env = new EnvironmentConfig {
                Registry = new RegistryConfig {
                    Provider = registryProvider,
                    Name = registry,
                    Project = project,
                    Region = region
                }
            };
            cfg.Environments[environment] = env;
        }

        env.Clusters[cluster] = new ClusterConfig {
            Provider = clusterProvider,
            Name = cluster,
            ResourceGroup = resourceGroup,
            Project = project,
            Region = region
        };
        _relay.SaveConfig(cfg);
        Console.Error.WriteLine($"Added cluster '{cluster}' to environment '{environment}'");
        return new RelayResult {
            Service = "*",
            Operation = "cluster add",
            TargetEnvironment = environment,
            SyncStatus = "configured"
        };
    }

    /// <summary>
    ///     Creates a Kubernetes cluster and container registry, then installs platform tools (ArgoCD, cert-manager).
    /// </summary>
    public RelayResult Create(string clusterProvider, string cluster, string environment = "dev", string domain = "", string registry = "", string registryProvider = "", string resourceGroup = "", string project = "", string region = "") {
        if (string.IsNullOrEmpty(registryProvider)) {
            registryProvider = clusterProvider;
        }

        string clusterCore = cluster;
        if (clusterCore.StartsWith("aks-")) {
            clusterCore = clusterCore["aks-".Length..];
        }

        if (clusterCore.StartsWith("gke-")) {
            clusterCore = clusterCore["gke-".Length..];
        }

        if (clusterProvider.ToLowerInvariant() is "azure" or "aks") {
            if (string.IsNullOrEmpty(resourceGroup)) {
                resourceGroup = $"rg-{clusterCore}";
            }

            if (string.IsNullOrEmpty(registry)) {
                registry = $"acr{clusterCore.Replace("-", "")}";
            }
        } else if (clusterProvider.ToLowerInvariant() is "gcp" or "gke") {
            if (string.IsNullOrEmpty(project)) {
                try {
                    project = CMD("gcloud config get-value project")
                        .Trim();
                } catch {
                    throw new InvalidOperationException("--project is required for GCP.");
                }
            }

            if (string.IsNullOrEmpty(region)) {
                try {
                    region = CMD("gcloud config get-value compute/region")
                        .Trim();
                } catch {
                    region = "us-central1";
                }
            }

            if (string.IsNullOrEmpty(registry)) {
                registry = $"{clusterCore}-repo";
            }
        }

        // Resolve compute SA and billing account for GCP
        string computeSa = "";
        string billingAccount = "";
        if (clusterProvider.ToLowerInvariant() is "gcp" or "gke") {
            try {
                computeSa = CMD($"gcloud projects describe {project} --format=value(projectNumber)")
                    .Trim() + "-compute@developer.gserviceaccount.com";
            } catch {
            }

            try {
                billingAccount = CMD("gcloud billing accounts list --format=value(ACCOUNT_ID) --filter=open=true --limit=1")
                    .Trim();
            } catch {
            }
        }

        var vars = new Dictionary<string, string> {
            ["cluster"] = cluster,
            ["registry"] = registry,
            ["project"] = project,
            ["region"] = region,
            ["resourceGroup"] = resourceGroup,
            ["domain"] = domain,
            ["minNodes"] = "1",
            ["maxNodes"] = "1",
            ["computeSa"] = computeSa,
            ["billingAccount"] = billingAccount
        };

        ClusterProvisionRunner.Provision(clusterProvider, vars, false);

        ClusterConfig clusterCfg = new() {
            Provider = clusterProvider,
            Name = cluster,
            ResourceGroup = resourceGroup,
            Project = project,
            Region = region
        };
        RegistryConfig registryCfg = new() {
            Provider = registryProvider,
            Name = registry,
            Project = project,
            Region = region
        };

        PlatformConfig cfg = _relay.LoadConfig();
        if (!cfg.Environments.TryGetValue(environment, out EnvironmentConfig? env)) {
            env = new EnvironmentConfig {
                Registry = registryCfg
            };
            cfg.Environments[environment] = env;
        }

        env.Clusters[cluster] = clusterCfg;
        _relay.SaveConfig(cfg);

        Console.Error.WriteLine($"Cluster '{cluster}' provisioned and added to environment '{environment}'");
        return new RelayResult {
            Service = "*",
            Operation = "cluster create",
            TargetEnvironment = environment,
            SyncStatus = "provisioned"
        };
    }

    /// <summary>
    ///     Installs platform tools (ArgoCD, cert-manager) on an existing cluster.
    /// </summary>
    public static RelayResult Install(string clusterProvider, string cluster, string environment = "dev", string resourceGroup = "", string project = "", string region = "") {
        var vars = new Dictionary<string, string> {
            ["cluster"] = cluster,
            ["project"] = project,
            ["region"] = region,
            ["resourceGroup"] = resourceGroup
        };

        ClusterProvisionRunner.Provision(clusterProvider, vars, true);

        Console.Error.WriteLine($"Platform tools installed on '{cluster}'");
        return new RelayResult {
            Service = "*",
            Operation = "cluster install",
            TargetEnvironment = environment,
            SyncStatus = "installed"
        };
    }

    /// <summary>
    ///     Deletes a cluster. Registry is preserved.
    /// </summary>
    public RelayResult Destroy(string cluster, string environment = "dev") {
        PlatformConfig cfg = _relay.LoadConfig();
        if (!cfg.Environments.TryGetValue(environment, out EnvironmentConfig? env)) {
            throw new InvalidOperationException($"Environment '{environment}' not found.");
        }

        if (!env.Clusters.TryGetValue(cluster, out ClusterConfig? clusterCfg)) {
            throw new InvalidOperationException($"Cluster '{cluster}' not found in environment '{environment}'.");
        }

        var vars = new Dictionary<string, string> {
            ["cluster"] = clusterCfg.Name,
            ["registry"] = env.Registry.Name,
            ["project"] = clusterCfg.Project,
            ["region"] = clusterCfg.Region,
            ["resourceGroup"] = clusterCfg.ResourceGroup
        };
        ClusterProvisionRunner.Destroy(clusterCfg.Provider, vars);

        env.Clusters.Remove(cluster);
        if (env.Clusters.Count == 0) {
            cfg.Environments.Remove(environment);
        }

        _relay.SaveConfig(cfg);

        Console.Error.WriteLine($"Cluster '{cluster}' destruction initiated");
        return new RelayResult {
            Service = "*",
            Operation = "cluster destroy",
            TargetEnvironment = environment,
            SyncStatus = "destroying"
        };
    }
}
