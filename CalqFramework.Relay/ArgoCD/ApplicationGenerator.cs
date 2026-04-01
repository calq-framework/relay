namespace CalqFramework.Relay.ArgoCD;

/// <summary>
///     Generates ArgoCD Application manifests for services.
/// </summary>
public static class ApplicationGenerator {
    public static string Generate(string serviceName, string repoUrl, string path, string targetRevision, string destServer, string destNamespace, bool autoSync = false, string ns = "argocd", bool ignoreSelectorDiff = false) {
        string syncPolicy = autoSync
            ? """
                  automated:
                    prune: true
                    selfHeal: true
              """
            : "    automated: null";

        string ignoreDiffs = ignoreSelectorDiff ? "  ignoreDifferences:\n    - group: \"\"\n      kind: Service\n      jsonPointers:\n        - /spec/selector\n" : "";

        return "apiVersion: argoproj.io/v1alpha1\n" + "kind: Application\n" + "metadata:\n" + $"  name: {serviceName}\n" + $"  namespace: {ns}\n" + "  finalizers:\n" + "    - resources-finalizer.argocd.argoproj.io\n" + "spec:\n" + "  project: default\n" +
               "  source:\n" + $"    repoURL: {repoUrl}\n" + $"    targetRevision: {targetRevision}\n" + $"    path: {path}\n" + "  destination:\n" + $"    server: {destServer}\n" + $"    namespace: {destNamespace}\n" + ignoreDiffs + "  syncPolicy:\n" +
               syncPolicy + "\n" + "    syncOptions:\n" + "      - CreateNamespace=true\n" + "      - PrunePropagationPolicy=foreground\n" + "      - PruneLast=true\n" + "    retry:\n" + "      limit: 3\n" + "      backoff:\n" + "        duration: 5s\n" +
               "        factor: 2\n" + "        maxDuration: 3m\n";
    }

    public static string GenerateRootApp(string platformName, string repoUrl, string targetRevision, string argocdPath, string destServer, string ns = "argocd") =>
        Generate($"{platformName}-root", repoUrl, argocdPath, targetRevision, destServer, ns, true, ns);
}
