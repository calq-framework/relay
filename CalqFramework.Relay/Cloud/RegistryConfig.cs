namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Cloud-agnostic container registry configuration.
/// </summary>
public class RegistryConfig {
    public string Provider { get; set; } = "azure";

    public string Name { get; set; } = "";

    // GCP
    public string Project { get; set; } = "";

    public string Region { get; set; } = "us";

    // Custom provider
    /// <summary>
    ///     Registry hostname (e.g., "123456789.dkr.ecr.us-east-1.amazonaws.com").
    ///     Required for custom providers.
    /// </summary>
    public string LoginServer { get; set; } = "";

    /// <summary>
    ///     Shell command to authenticate to the registry.
    ///     Placeholders: {name}, {loginServer}, {project}, {region}.
    /// </summary>
    public string AuthCommand { get; set; } = "";

    /// <summary>
    ///     Shell command to import an image from another registry.
    ///     Placeholders: {source}, {target}.
    ///     Default: pull + tag + push.
    /// </summary>
    public string ImportCommand { get; set; } = "";
}
