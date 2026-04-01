namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Executes configurable provisioning steps from ClusterProvisionConfig.
///     Loads config from .relay/config/ with cloud provider as preset name.
///     Falls back to embedded defaults if no config file exists.
/// </summary>
public static class ClusterProvisionRunner {
    private static readonly string ConfigDir = Path.Combine(".relay", "config");

    public static void Provision(string provider, Dictionary<string, string> vars, bool postStepsOnly = false) {
        ClusterProvisionConfig config = LoadConfig(provider);

        if (!postStepsOnly) {
            RunSteps(config.Steps, vars);
        }

        // Re-resolve late-binding variables after project/cluster creation
        if (provider.ToLowerInvariant() is "gcp" or "gke" && string.IsNullOrEmpty(vars.GetValueOrDefault("computeSa"))) {
            try {
                string projectNumber = CMD($"gcloud projects describe {vars["project"]} --format=value(projectNumber)")
                    .Trim();
                vars["computeSa"] = $"{projectNumber}-compute@developer.gserviceaccount.com";
            } catch {
            }
        }

        // Authenticate to the cluster before post-steps
        string authCmd = provider.ToLowerInvariant() switch {
            "gcp" or "gke" => $"gcloud container clusters get-credentials {vars["cluster"]} --region {vars["region"]} --project {vars["project"]}",
            "azure" or "aks" => $"az aks get-credentials --resource-group {vars["resourceGroup"]} --name {vars["cluster"]} --overwrite-existing",
            _ => ""
        };
        if (!string.IsNullOrEmpty(authCmd)) {
            RUN(authCmd);
        }

        RunSteps(config.PostSteps, vars);
    }

    public static void Destroy(string provider, Dictionary<string, string> vars) {
        ClusterProvisionConfig config = LoadConfig(provider);
        RunSteps(config.DestroySteps, vars);
    }

    private static void RunSteps(List<ProvisionStep> steps, Dictionary<string, string> vars) {
        foreach (ProvisionStep step in steps) {
            string cmd = step.Command;
            foreach (KeyValuePair<string, string> kvp in vars) {
                cmd = cmd.Replace($"{{{kvp.Key}}}", kvp.Value);
            }

            try {
                RUN(cmd);
            } catch {
                if (step.ContinueOnError) {
                    Console.Error.WriteLine("(continuing)");
                } else {
                    throw;
                }
            }
        }
    }

    private static ClusterProvisionConfig LoadConfig(string provider) {
        string configPath = Path.Combine(ConfigDir, $"ClusterProvisionConfig.{provider}.json");

        if (File.Exists(configPath)) {
            string json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<ClusterProvisionConfig>(
                json,
                new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                }) ?? GetDefault(provider);
        }

        // Write embedded default to disk so user can see and edit it
        ClusterProvisionConfig config = GetDefault(provider);
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(
            configPath,
            JsonSerializer.Serialize(
                config,
                new JsonSerializerOptions {
                    WriteIndented = true
                }));
        Console.Error.WriteLine($"Created {configPath} with default {provider} steps");
        return config;
    }

    private static ClusterProvisionConfig GetDefault(string provider) => provider.ToLowerInvariant() switch {
        "gcp" or "gke" => ClusterProvisionDefaults.Gcp(),
        "azure" or "aks" => ClusterProvisionDefaults.Azure(),
        _ => new ClusterProvisionConfig()
    };
}
