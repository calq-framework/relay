namespace CalqFramework.Relay.Cloud.Custom;

/// <summary>
///     Authenticates to a cluster using configurable shell commands.
///     Enables support for any cloud provider (AWS, DigitalOcean, etc.)
///     without compiled code changes.
/// </summary>
public class CustomAuthenticator : IClusterAuthenticator {
    public void Authenticate(ClusterConfig cluster) {
        if (string.IsNullOrEmpty(cluster.AuthCommand)) {
            throw new InvalidOperationException("ClusterConfig.AuthCommand is required for custom providers.");
        }

        RUN(ReplacePlaceholders(cluster.AuthCommand, cluster));
    }

    public string GetContextName(ClusterConfig cluster) {
        if (string.IsNullOrEmpty(cluster.ContextName)) {
            return cluster.Name;
        }

        return ReplacePlaceholders(cluster.ContextName, cluster);
    }

    private static string ReplacePlaceholders(string template, ClusterConfig cluster) =>
        template.Replace("{name}", cluster.Name)
            .Replace("{resourceGroup}", cluster.ResourceGroup)
            .Replace("{project}", cluster.Project)
            .Replace("{region}", cluster.Region);
}
