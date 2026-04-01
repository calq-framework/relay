namespace CalqFramework.Relay.Platform;

/// <summary>
///     Generates a cluster-wide CronJob that auto-tunes resource requests
///     based on observed CPU usage. Keeps HPA utilization calculations accurate
///     without manual resource configuration.
/// </summary>
public static class AdaptiveScalerGenerator {
    private const string ComponentName = "adaptive-scaler";

    /// <summary>
    ///     Generates all Kubernetes manifests for the adaptive scaler.
    /// </summary>
    /// <param name="ns">Namespace for the scaler resources.</param>
    /// <param name="schedule">CronJob schedule expression.</param>
    /// <param name="kubectlImage">kubectl container image URL.</param>
    public static string Generate(string ns = "calq-relay-system", string schedule = "* * * * *", string kubectlImage = "bitnami/kubectl:latest") {
        string script = GetScript();

        return "---\n" + "apiVersion: v1\n" + "kind: Namespace\n" + "metadata:\n" + $"  name: {ns}\n" + "---\n" + "apiVersion: v1\n" + "kind: ServiceAccount\n" + "metadata:\n" + $"  name: {ComponentName}\n" + $"  namespace: {ns}\n" + "---\n" +
               "apiVersion: rbac.authorization.k8s.io/v1\n" + "kind: ClusterRole\n" + "metadata:\n" + $"  name: {ComponentName}\n" + "rules:\n" + "  - apiGroups: [\"\"]\n" + "    resources: [\"namespaces\", \"pods\"]\n" + "    verbs: [\"list\"]\n" +
               "  - apiGroups: [\"apps\"]\n" + "    resources: [\"deployments\"]\n" + "    verbs: [\"get\", \"list\", \"patch\"]\n" + "  - apiGroups: [\"metrics.k8s.io\"]\n" + "    resources: [\"pods\"]\n" + "    verbs: [\"list\"]\n" + "---\n" +
               "apiVersion: rbac.authorization.k8s.io/v1\n" + "kind: ClusterRoleBinding\n" + "metadata:\n" + $"  name: {ComponentName}\n" + "roleRef:\n" + "  apiGroup: rbac.authorization.k8s.io\n" + "  kind: ClusterRole\n" + $"  name: {ComponentName}\n" +
               "subjects:\n" + "  - kind: ServiceAccount\n" + $"    name: {ComponentName}\n" + $"    namespace: {ns}\n" + "---\n" + "apiVersion: batch/v1\n" + "kind: CronJob\n" + "metadata:\n" + $"  name: {ComponentName}\n" + $"  namespace: {ns}\n" +
               "spec:\n" + $"  schedule: \"{schedule}\"\n" + "  successfulJobsHistoryLimit: 1\n" + "  failedJobsHistoryLimit: 1\n" + "  concurrencyPolicy: Forbid\n" + "  jobTemplate:\n" + "    spec:\n" + "      template:\n" + "        spec:\n" +
               $"          serviceAccountName: {ComponentName}\n" + "          restartPolicy: Never\n" + "          containers:\n" + $"            - name: {ComponentName}\n" + $"              image: {kubectlImage}\n" +
               "              command: [\"/bin/sh\", \"-c\"]\n" + "              args:\n" + "                - |\n" + script;
    }

    private static string GetScript() =>
        "                  # Iterate managed namespaces\n" + "                  for NS in $(kubectl get namespaces -o jsonpath='{.items[*].metadata.name}'); do\n" +
        "                    case \"$NS\" in kube-*|calq-relay-*|argocd|cert-manager|external-dns) continue;; esac\n" + "                    \n" + "                    # Get node allocatable CPU (first node, in millicores)\n" +
        "                    NODE_CPU=$(kubectl get nodes -o jsonpath='{.items[0].status.allocatable.cpu}' | sed 's/m//')\n" + "                    [ -z \"$NODE_CPU\" ] && continue\n" +
        "                    # If value is in cores (no 'm' suffix), convert to millicores\n" + "                    case \"$NODE_CPU\" in *[!0-9]*) ;; *) NODE_CPU=$((NODE_CPU * 1000));; esac\n" + "                    \n" +
        "                    # For each deployment with relay.calq.io/scaling=adaptive annotation\n" +
        "                    for DEPLOY in $(kubectl get deployments -n \"$NS\" -o jsonpath='{range .items[*]}{.metadata.name}{\"\\n\"}{end}' 2>/dev/null); do\n" + "                      [ -z \"$DEPLOY\" ] && continue\n" + "                      \n" +
        "                      SCALING=$(kubectl get deployment \"$DEPLOY\" -n \"$NS\" -o jsonpath='{.metadata.annotations.relay\\.calq\\.io/scaling}' 2>/dev/null)\n" + "                      [ \"$SCALING\" != \"adaptive\" ] && continue\n" +
        "                      \n" + "                      # Get average CPU usage across pods (in millicores)\n" + "                      APP_LABEL=$(kubectl get deployment \"$DEPLOY\" -n \"$NS\" -o jsonpath='{.spec.selector.matchLabels.app}')\n" +
        "                      [ -z \"$APP_LABEL\" ] && continue\n" + "                      \n" + "                      TOTAL_CPU=0\n" + "                      POD_COUNT=0\n" +
        "                      for CPU in $(kubectl top pods -n \"$NS\" -l \"app=$APP_LABEL\" --no-headers 2>/dev/null | awk '{print $2}' | sed 's/m//'); do\n" + "                        TOTAL_CPU=$((TOTAL_CPU + CPU))\n" +
        "                        POD_COUNT=$((POD_COUNT + 1))\n" + "                      done\n" + "                      [ \"$POD_COUNT\" -eq 0 ] && continue\n" + "                      \n" +
        "                      AVG_CPU=$((TOTAL_CPU / POD_COUNT))\n" + "                      # Set request to avg * 1.2 (20% headroom), minimum 10m\n" + "                      NEW_REQUEST=$(( (AVG_CPU * 120 + 50) / 100 ))\n" +
        "                      [ \"$NEW_REQUEST\" -lt 10 ] && NEW_REQUEST=10\n" + "                      \n" + "                      # Get current request\n" +
        "                      CURRENT_REQUEST=$(kubectl get deployment \"$DEPLOY\" -n \"$NS\" -o jsonpath='{.spec.template.spec.containers[0].resources.requests.cpu}' 2>/dev/null | sed 's/m//')\n" +
        "                      [ -z \"$CURRENT_REQUEST\" ] && CURRENT_REQUEST=0\n" + "                      \n" + "                      # Only patch if difference > 10%\n" +
        "                      if [ \"$CURRENT_REQUEST\" -eq 0 ] || [ $((NEW_REQUEST * 100 / CURRENT_REQUEST)) -gt 110 ] || [ $((NEW_REQUEST * 100 / CURRENT_REQUEST)) -lt 90 ]; then\n" +
        "                        kubectl patch deployment \"$DEPLOY\" -n \"$NS\" -p '{\"spec\":{\"template\":{\"spec\":{\"containers\":[{\"name\":\"'\"$APP_LABEL\"'\",\"resources\":{\"requests\":{\"cpu\":\"'\"${NEW_REQUEST}m\"'\"}}}]}}}}'\n" +
        "                        echo \"Adaptive: $NS/$DEPLOY request ${CURRENT_REQUEST}m -> ${NEW_REQUEST}m (avg: ${AVG_CPU}m)\"\n" + "                      fi\n" + "                    done\n" + "                  done";
}
