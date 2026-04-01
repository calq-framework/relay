namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Cloud-agnostic cluster connection configuration.
/// </summary>
public class ClusterConfig {
    public string Provider { get; set; } = "azure";
    public string Name { get; set; } = "";

    public string ServerUrl { get; set; } = "";

    // Azure
    public string ResourceGroup { get; set; } = "";

    // GCP
    public string Project { get; set; } = "";

    public string Region { get; set; } = "";

    // Custom provider
    /// <summary>
    ///     Shell command to authenticate to the cluster.
    ///     Placeholders: {name}, {resourceGroup}, {project}, {region}.
    /// </summary>
    public string AuthCommand { get; set; } = "";

    /// <summary>
    ///     kubectl context name after authentication.
    ///     Placeholders: {name}, {resourceGroup}, {project}, {region}.
    /// </summary>
    public string ContextName { get; set; } = "";

    /// <summary>
    ///     GitHub Actions login step for workflow scaffolding.
    /// </summary>
    public WorkflowLoginConfig? WorkflowLogin { get; set; }
}

/// <summary>
///     Structured GitHub Actions step for cloud authentication in workflows.
/// </summary>
public class WorkflowLoginConfig {
    public string Action { get; set; } = "";
    public Dictionary<string, string> With { get; set; } = [];
}
