namespace CalqFramework.Relay.Platform;

/// <summary>
///     Generates a cluster-wide pod recycler that marks the most recently created
///     pod in each HPA-managed deployment with a low deletion cost, ensuring
///     autoscaler scale-down events remove the newest pods first (preserving
///     warmer, more stable older pods).
/// </summary>
public static class PodRecyclerGenerator {
    private const string ComponentName = "pod-recycler";

    /// <summary>
    ///     Generates all Kubernetes manifests for the cluster-wide pod recycler.
    ///     Uses kubectl (via bitnami/kubectl image) for reliable JSON field extraction.
    /// </summary>
    /// <param name="ns">Namespace for the recycler resources.</param>
    /// <param name="schedule">CronJob schedule expression.</param>
    /// <param name="kubectlImage">kubectl container image URL. Defaults to bitnami/kubectl:latest.</param>
    public static string Generate(string ns = "calq-relay-system", string schedule = "*/5 * * * *", string kubectlImage = "bitnami/kubectl:latest") {
        string script = GetScript(ns);

        return $"""
                ---
                apiVersion: v1
                kind: Namespace
                metadata:
                  name: {ns}
                ---
                apiVersion: v1
                kind: ServiceAccount
                metadata:
                  name: {ComponentName}
                  namespace: {ns}
                ---
                apiVersion: rbac.authorization.k8s.io/v1
                kind: ClusterRole
                metadata:
                  name: {ComponentName}
                rules:
                  - apiGroups: [""]
                    resources: ["namespaces"]
                    verbs: ["list"]
                  - apiGroups: ["autoscaling"]
                    resources: ["horizontalpodautoscalers"]
                    verbs: ["get", "list"]
                  - apiGroups: ["apps"]
                    resources: ["deployments"]
                    verbs: ["get"]
                  - apiGroups: [""]
                    resources: ["pods"]
                    verbs: ["list", "patch"]
                ---
                apiVersion: rbac.authorization.k8s.io/v1
                kind: ClusterRoleBinding
                metadata:
                  name: {ComponentName}
                roleRef:
                  apiGroup: rbac.authorization.k8s.io
                  kind: ClusterRole
                  name: {ComponentName}
                subjects:
                  - kind: ServiceAccount
                    name: {ComponentName}
                    namespace: {ns}
                ---
                apiVersion: batch/v1
                kind: CronJob
                metadata:
                  name: {ComponentName}
                  namespace: {ns}
                spec:
                  schedule: "{schedule}"
                  successfulJobsHistoryLimit: 1
                  failedJobsHistoryLimit: 1
                  concurrencyPolicy: Forbid
                  jobTemplate:
                    spec:
                      template:
                        spec:
                          serviceAccountName: {ComponentName}
                          restartPolicy: Never
                          containers:
                            - name: {ComponentName}
                              image: {kubectlImage}
                              command: ["/bin/sh", "-c"]
                              args:
                                - |
                {script}
                """;
    }

    private static string GetScript(string ns) =>
        "                  # Iterate all non-system namespaces\n" + "                  for NS in $(kubectl get namespaces -o jsonpath='{.items[*].metadata.name}'); do\n" + "                    case \"$NS\" in kube-*|" + ns + ") continue;; esac\n" +
        "                    \n" + "                    # List HPAs in this namespace\n" + "                    for HPA_NAME in $(kubectl get hpa -n \"$NS\" -o jsonpath='{.items[*].metadata.name}' 2>/dev/null); do\n" +
        "                      [ -z \"$HPA_NAME\" ] && continue\n" + "                      \n" + "                      # Get the HPA's target deployment\n" +
        "                      DEPLOY_NAME=$(kubectl get hpa \"$HPA_NAME\" -n \"$NS\" -o jsonpath='{.spec.scaleTargetRef.name}')\n" + "                      [ -z \"$DEPLOY_NAME\" ] && continue\n" + "                      \n" +
        "                      # Get the deployment's app label selector\n" + "                      SELECTOR=$(kubectl get deployment \"$DEPLOY_NAME\" -n \"$NS\" -o jsonpath='{.spec.selector.matchLabels.app}' 2>/dev/null)\n" +
        "                      [ -z \"$SELECTOR\" ] && continue\n" + "                      \n" + "                      # Find the most recently created pod (sort -r = newest first)\n" +
        "                      NEWEST_POD=$(kubectl get pods -n \"$NS\" -l \"app=$SELECTOR\" --sort-by=.metadata.creationTimestamp -o jsonpath='{.items[-1:].metadata.name}')\n" + "                      [ -z \"$NEWEST_POD\" ] && continue\n" +
        "                      \n" + "                      # Mark it for preferred deletion on scale-down\n" + "                      kubectl annotate pod \"$NEWEST_POD\" -n \"$NS\" controller.kubernetes.io/pod-deletion-cost=\"-1\" --overwrite\n" +
        "                      echo \"Marked $NS/$NEWEST_POD for preferred deletion (deploy: $DEPLOY_NAME)\"\n" + "                    done\n" + "                  done";
}
