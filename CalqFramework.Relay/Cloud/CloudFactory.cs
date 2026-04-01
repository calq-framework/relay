using CalqFramework.Relay.Cloud.Azure;
using CalqFramework.Relay.Cloud.Custom;
using CalqFramework.Relay.Cloud.Gcp;

namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Creates cloud-specific implementations from configuration.
/// </summary>
public static class CloudFactory {
    public static IClusterAuthenticator CreateAuthenticator(string provider) => provider.ToLowerInvariant() switch {
        "azure" or "aks" => new AksAuthenticator(),
        "gcp" or "gke" => new GkeAuthenticator(),
        _ => new CustomAuthenticator()
    };

    public static IContainerRegistry CreateRegistry(RegistryConfig config) => config.Provider.ToLowerInvariant() switch {
        "azure" or "acr" => new AcrRegistry(config),
        "gcp" or "gar" => new GarRegistry(config),
        _ => new CustomRegistry(config)
    };
}
