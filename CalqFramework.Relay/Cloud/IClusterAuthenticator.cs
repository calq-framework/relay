namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Authenticates to a Kubernetes cluster and sets up kubectl context.
/// </summary>
public interface IClusterAuthenticator {
    void Authenticate(ClusterConfig cluster);
    string GetContextName(ClusterConfig cluster);
}
