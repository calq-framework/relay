namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Configurable cluster provisioning steps. Each cloud provider has its own
///     preset file (e.g., ClusterProvisionConfig.gcp.json).
/// </summary>
public class ClusterProvisionConfig {
    public List<ProvisionStep> Steps { get; set; } = [];
    public List<ProvisionStep> DestroySteps { get; set; } = [];
    public List<ProvisionStep> PostSteps { get; set; } = [];
}

public class ProvisionStep {
    public string Command { get; set; } = "";
    public bool ContinueOnError { get; set; }
}
