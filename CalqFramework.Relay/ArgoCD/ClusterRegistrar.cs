using CalqFramework.Relay.Cloud;

namespace CalqFramework.Relay.ArgoCD;

/// <summary>
///     Registers Kubernetes clusters with ArgoCD.
/// </summary>
public static class ClusterRegistrar {
    public static void Register(ClusterConfig cluster, string contextName) {
        try {
            CMD($"argocd cluster get {cluster.ServerUrl} --port-forward --port-forward-namespace argocd");
        } catch {
            RUN($"argocd cluster add {contextName} --name {cluster.Name} --yes --port-forward --port-forward-namespace argocd");
        }
    }

    public static string GenerateClusterSecret(string name, string serverUrl, string caData, string bearerToken, string ns = "argocd") =>
        $$"""
          apiVersion: v1
          kind: Secret
          metadata:
            name: cluster-{{name}}
            namespace: {{ns}}
            labels:
              argocd.argoproj.io/secret-type: cluster
          type: Opaque
          stringData:
            name: "{{name}}"
            server: "{{serverUrl}}"
            config: |
              {
                "bearerToken": "{{bearerToken}}",
                "tlsClientConfig": {
                  "insecure": false,
                  "caData": "{{caData}}"
                }
              }
          """;
}
