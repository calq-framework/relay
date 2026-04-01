namespace CalqFramework.Relay.Cloud.Custom;

/// <summary>
///     Container registry backed by configurable shell commands.
///     Enables support for any registry (ECR, Docker Hub, Harbor, etc.)
///     without compiled code changes.
/// </summary>
public class CustomRegistry : IContainerRegistry {
    private readonly RegistryConfig _config;

    public CustomRegistry(RegistryConfig config) {
        if (string.IsNullOrEmpty(config.LoginServer)) {
            throw new InvalidOperationException("RegistryConfig.LoginServer is required for custom providers.");
        }

        _config = config;
    }

    public string LoginServer => _config.LoginServer;

    public void Authenticate() {
        if (!string.IsNullOrEmpty(_config.AuthCommand)) {
            RUN(ReplacePlaceholders(_config.AuthCommand));
        }
    }

    public string GetImageUrl(string imageName, string tag) => $"{LoginServer}/{imageName}:{tag}";

    public bool ImageExists(string imageRef) =>
        // Custom registries don't have a standard way to check — assume it doesn't exist
        false;

    public void ImportImage(string sourceImageUrl, IContainerRegistry? sourceRegistry = null) {
        string imageRef = sourceImageUrl.Contains('/') ? sourceImageUrl[(sourceImageUrl.IndexOf('/') + 1)..] : sourceImageUrl;
        string target = $"{LoginServer}/{imageRef}";

        if (!string.IsNullOrEmpty(_config.ImportCommand)) {
            RUN(
                _config.ImportCommand.Replace("{source}", sourceImageUrl)
                    .Replace("{target}", target));
        } else {
            RUN($"docker pull {sourceImageUrl}");
            RUN($"docker tag {sourceImageUrl} {target}");
            RUN($"docker push {target}");
        }
    }

    private string ReplacePlaceholders(string template) =>
        template.Replace("{name}", _config.Name)
            .Replace("{loginServer}", _config.LoginServer)
            .Replace("{project}", _config.Project)
            .Replace("{region}", _config.Region);
}
