namespace CalqFramework.Relay.ArgoCD;

/// <summary>
///     Installs and configures ArgoCD on the management cluster via Helm.
/// </summary>
public static class ArgoCDInstaller {
    public static void Install(string ns = "argocd", string chartVersion = "") {
        RUN($"kubectl create namespace {ns} --dry-run=client -o yaml | kubectl apply -f -");
        RUN("helm repo add argo https://argoproj.github.io/argo-helm");
        RUN("helm repo update argo");
        string versionFlag = string.IsNullOrEmpty(chartVersion) ? "" : $"--version {chartVersion}";
        RUN($"helm upgrade --install argocd argo/argo-cd --namespace {ns} {versionFlag} " + "--set server.extraArgs[0]=--insecure " + "--set configs.params.application\\.resourceTrackingMethod=annotation " + "--wait --timeout 5m");
    }

    public static string GetInitialPassword(string ns = "argocd") {
        RUN($"kubectl wait --for=condition=available deployment/argocd-server -n {ns} --timeout=300s");
        string encoded = CMD($"kubectl -n {ns} get secret argocd-initial-admin-secret -o jsonpath=\"{{.data.password}}\"")
            .Trim();
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    public static void Login(string ns = "argocd", string password = "") {
        if (string.IsNullOrEmpty(password)) {
            password = GetInitialPassword(ns);
        }

        // Use --port-forward to avoid needing a persistent port-forward or in-cluster DNS
        RUN($"argocd login --port-forward --port-forward-namespace {ns} --username admin --password {password} --insecure --plaintext");
    }
}
