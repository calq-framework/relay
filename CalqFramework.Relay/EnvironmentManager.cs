using CalqFramework.Relay.Cloud;

namespace CalqFramework.Relay;

/// <summary>
///     Manage environments: clone and tear down.
/// </summary>
public class EnvironmentManager {
    private readonly RelayManager _relay;
    internal EnvironmentManager(RelayManager relay) => _relay = relay;

    /// <summary>
    ///     Creates a copy of an environment for PR preview or testing.
    /// </summary>
    /// <param name="environment">Target environment name (e.g., pr-42).</param>
    /// <param name="baseEnvironment">Base environment to clone from (e.g., dev).</param>
    /// <param name="dryRun">Log actions without applying.</param>
    public RelayResult Clone(string environment, string baseEnvironment = "dev", bool dryRun = false) {
        PlatformConfig cfg = _relay.LoadConfig();
        EnvironmentConfig env = cfg.Environments[baseEnvironment];
        string srcNs = RelayManager.GetNamespace(cfg, baseEnvironment);
        string tgtNs = RelayManager.GetNamespace(cfg, environment);
        ClusterConfig clusterCfg = env.Clusters.Values.First();
        RelayManager.AuthenticateCluster(clusterCfg);

        if (!dryRun) {
            RUN($"kubectl create namespace {tgtNs} --dry-run=client -o yaml | kubectl apply -f -");
        }

        Console.Error.WriteLine($"Cloning {srcNs} -> {tgtNs}");
        foreach (KeyValuePair<string, ServiceConfig> kvp in cfg.Services) {
            string svcName = kvp.Key;
            Console.Error.WriteLine($"--- Cloning {svcName} ---");
            try {
                string resourceTypes = CMD("kubectl api-resources --verbs=list --namespaced -o name");
                string yaml = "";
                foreach (string rt in resourceTypes.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
                    string r = rt.Trim();
                    if (r is "events" or "events.events.k8s.io" or "jobs.batch" or "endpoints" or "endpointslices.discovery.k8s.io") {
                        continue;
                    }

                    try {
                        string chunk = CMD($"kubectl get {r} -n {srcNs} -l app={svcName} --ignore-not-found -o yaml");
                        if (!string.IsNullOrWhiteSpace(chunk) && chunk.Contains("items:") && !chunk.Contains("items: []")) {
                            yaml += $"---\n{chunk}\n";
                        }
                    } catch {
                    }
                }

                if (!dryRun && !string.IsNullOrWhiteSpace(yaml)) {
                    string tmpFile = Path.GetTempFileName();
                    File.WriteAllText(tmpFile, yaml);
                    RUN(
                        $"yq eval '.items.[] | split_doc' {tmpFile} | yq eval 'del(.metadata.namespace, .metadata.resourceVersion, .metadata.uid, .metadata.creationTimestamp, .status, .spec.clusterIP, .spec.clusterIPs, .spec.ports[].nodePort, .metadata.annotations.\"kubectl.kubernetes.io/last-applied-configuration\")' - | kubectl apply -n {tgtNs} -f -");
                    File.Delete(tmpFile);
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"Failed to clone {svcName}: {ex.Message}");
            }
        }

        return new RelayResult {
            Service = "*",
            Operation = "environment clone",
            TargetEnvironment = environment,
            SyncStatus = dryRun ? "dry-run" : "cloned",
            DryRun = dryRun
        };
    }

    /// <summary>
    ///     Tears down an environment and deletes all its cluster resources.
    /// </summary>
    public RelayResult Remove(string environment, string baseEnvironment = "dev") {
        PlatformConfig cfg = _relay.LoadConfig();
        string ns = RelayManager.GetNamespace(cfg, environment);

        Dictionary<string, ClusterConfig> clusters;
        if (cfg.Environments.TryGetValue(environment, out EnvironmentConfig? env)) {
            clusters = env.Clusters;
        } else {
            clusters = cfg.Environments[baseEnvironment].Clusters;
        }

        foreach (KeyValuePair<string, ClusterConfig> kvp in clusters) {
            try {
                RelayManager.AuthenticateCluster(kvp.Value);
                RUN($"kubectl delete namespace {ns} --ignore-not-found");
                Console.Error.WriteLine($"Deleted namespace {ns} on {kvp.Key}");
            } catch {
                Console.Error.WriteLine($"Could not delete namespace {ns} on {kvp.Key}");
            }
        }

        if (cfg.Environments.Remove(environment)) {
            _relay.SaveConfig(cfg);
            Console.Error.WriteLine($"Removed environment '{environment}' from {_relay.Config}");
        }

        string argocdDir = Path.Combine(PWD, ".relay", "apps");
        if (Directory.Exists(argocdDir)) {
            foreach (string file in Directory.GetFiles(argocdDir, "*.yaml")) {
                try {
                    string content = File.ReadAllText(file);
                    if (content.Contains($"namespace: {ns}")) {
                        File.Delete(file);
                        Console.Error.WriteLine($"Deleted {file}");
                    }
                } catch {
                }
            }
        }

        return new RelayResult {
            Service = "*",
            Operation = "environment remove",
            TargetEnvironment = environment,
            SyncStatus = "deleted"
        };
    }
}
