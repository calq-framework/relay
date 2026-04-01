using CalqFramework.Relay.Cloud;

namespace CalqFramework.Relay;

/// <summary>
///     Top-level platform configuration. Defines environments (with one or more
///     clusters), registries, and services managed by the relay.
/// </summary>
public class PlatformConfig {
    public string Name { get; set; } = "";
    public ArgoCDConfig ArgoCD { get; set; } = new();
    public Dictionary<string, EnvironmentConfig> Environments { get; set; } = [];
    public Dictionary<string, NodePoolConfig> NodePools { get; set; } = [];
    public Dictionary<string, ServiceConfig> Services { get; set; } = [];
}

public class ArgoCDConfig {
    public string Namespace { get; set; } = "argocd";
    public string ChartVersion { get; set; } = "";
    public string InstallCluster { get; set; } = "";
    public bool PodRecycling { get; set; } = true;
    public bool CanaryEnforcement { get; set; } = true;
}

/// <summary>
///     An environment has one registry (shared across regions) and one or more
///     clusters (for multi-region deployments).
/// </summary>
public class EnvironmentConfig {
    public Dictionary<string, ClusterConfig> Clusters { get; set; } = [];
    public RegistryConfig Registry { get; set; } = new();
}

public class ServiceConfig {
    public string Path { get; set; } = "";
    public string Project { get; set; } = "";
    public bool BlueGreen { get; set; }
    public string NodePool { get; set; } = "";
    public int MinReplicas { get; set; }
    public int MaxReplicas { get; set; }
    public BuildConfig Build { get; set; } = new();
}

public class NodePoolConfig {
    public ScalingMode Scaling { get; set; } = ScalingMode.Grouped;
    public int MinNodes { get; set; } = 1;
    public int MaxNodes { get; set; } = 10;
    public int TargetUtilization { get; set; } = 80;
}

public enum ScalingMode {
    None,
    Grouped,
    Adaptive
}
