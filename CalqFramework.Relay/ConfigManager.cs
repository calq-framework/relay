namespace CalqFramework.Relay;

/// <summary>
///     Manage configuration: sync with organization repo.
/// </summary>
public class ConfigManager {
    private static readonly string ConfigDir = System.IO.Path.Combine(".relay", "config");
    private readonly RelayManager _relay;
    internal ConfigManager(RelayManager relay) => _relay = relay;

    /// <summary>
    ///     Prints the configuration directory path.
    /// </summary>
    public static string Location() => System.IO.Path.GetFullPath(ConfigDir);

    /// <summary>
    ///     Pulls configuration from the organization's .relay repo.
    /// </summary>
    public static RelayResult Pull() {
        string org = ResolveOrg();
        string tempDir = System.IO.Path.GetTempFileName();
        File.Delete(tempDir);

        try {
            RUN($"git clone --depth 1 https://x-access-token:{GetGhToken()}@github.com/{org}/.relay.git {tempDir}");
        } catch {
            Console.Error.WriteLine($"Organization repo '{org}/.relay' not found.");
            return new RelayResult {
                Operation = "config pull",
                SyncStatus = "not-found"
            };
        }

        string srcConfig = System.IO.Path.Combine(tempDir, "config");
        if (Directory.Exists(srcConfig)) {
            Directory.CreateDirectory(ConfigDir);
            foreach (string file in Directory.GetFiles(srcConfig, "*.json")) {
                string dest = System.IO.Path.Combine(ConfigDir, System.IO.Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                Console.Error.WriteLine($"Pulled {System.IO.Path.GetFileName(file)}");
            }
        }

        try {
            Directory.Delete(tempDir, true);
        } catch {
        }

        return new RelayResult {
            Operation = "config pull",
            SyncStatus = "synced"
        };
    }

    /// <summary>
    ///     Pushes local configuration to the organization's .relay repo. Creates a PR by default.
    /// </summary>
    /// <param name="direct">Push directly to main instead of creating a PR.</param>
    public static RelayResult Push(bool direct = false) {
        if (!Directory.Exists(ConfigDir) || Directory.GetFiles(ConfigDir, "*.json")
                .Length == 0) {
            Console.Error.WriteLine("No config files to push.");
            return new RelayResult {
                Operation = "config push",
                SyncStatus = "empty"
            };
        }

        string org = ResolveOrg();
        string tempDir = System.IO.Path.GetTempFileName();
        File.Delete(tempDir);

        try {
            RUN($"git clone --depth 1 https://x-access-token:{GetGhToken()}@github.com/{org}/.relay.git {tempDir}");
        } catch {
            Console.Error.WriteLine($"Creating organization repo '{org}/.relay'...");
            RUN($"gh repo create {org}/.relay --private");
            RUN($"git clone https://x-access-token:{GetGhToken()}@github.com/{org}/.relay.git {tempDir}");
        }

        string destConfig = System.IO.Path.Combine(tempDir, "config");
        Directory.CreateDirectory(destConfig);
        foreach (string file in Directory.GetFiles(ConfigDir, "*.json")) {
            File.Copy(file, System.IO.Path.Combine(destConfig, System.IO.Path.GetFileName(file)), overwrite: true);
        }

        try {
            RUN($"git -C {tempDir} add -A");
            RUN($"git -C {tempDir} diff --cached --quiet");
            Console.Error.WriteLine("No changes to push.");
        } catch {
            string ts = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            if (direct) {
                RUN($"git -C {tempDir} commit -m \"calq-relay config push\"");
                RUN($"git -C {tempDir} push");
                Console.Error.WriteLine($"Pushed config to {org}/.relay");
            } else {
                string branch = $"config-update-{ts}";
                RUN($"git -C {tempDir} checkout -b {branch}");
                RUN($"git -C {tempDir} commit -m \"calq-relay config update\"");
                RUN($"git -C {tempDir} push -u origin {branch}");
                RUN($"gh pr create --repo {org}/.relay --title \"calq-relay config update\" --body \"\" --head {branch}");
                Console.Error.WriteLine($"Created PR in {org}/.relay");
            }
        }

        try {
            Directory.Delete(tempDir, true);
        } catch {
        }

        return new RelayResult {
            Operation = "config push",
            SyncStatus = direct ? "pushed" : "pr-created"
        };
    }

    private static string ResolveOrg() {
        try {
            string remote = CMD("git remote get-url origin")
                .Trim();
            // Extract org from https://github.com/org/repo.git or git@github.com:org/repo.git
            if (remote.Contains("github.com/")) {
                return remote.Split("github.com/")[1]
                    .Split('/')[0];
            }

            if (remote.Contains("github.com:")) {
                return remote.Split("github.com:")[1]
                    .Split('/')[0];
            }
        } catch {
        }

        throw new InvalidOperationException("Could not determine organization from git remote.");
    }

    private static string GetGhToken() {
        try {
            return CMD("gh auth token")
                .Trim();
        } catch {
            return "";
        }
    }
}
