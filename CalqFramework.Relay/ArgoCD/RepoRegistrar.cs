namespace CalqFramework.Relay.ArgoCD;

/// <summary>
///     Registers Git repositories with ArgoCD.
/// </summary>
public static class RepoRegistrar {
    public static void RegisterHttps(string repoUrl, string username, string password) {
        try {
            CMD($"argocd repo get {repoUrl} --port-forward --port-forward-namespace argocd");
        } catch {
            RUN($"argocd repo add {repoUrl} --username {username} --password {password} --port-forward --port-forward-namespace argocd");
        }
    }

    public static void RegisterSsh(string repoUrl, string sshPrivateKeyPath) {
        try {
            CMD($"argocd repo get {repoUrl} --port-forward --port-forward-namespace argocd");
        } catch {
            RUN($"argocd repo add {repoUrl} --ssh-private-key-path {sshPrivateKeyPath} --port-forward --port-forward-namespace argocd");
        }
    }
}
