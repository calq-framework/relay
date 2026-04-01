namespace CalqFramework.Relay.Platform;

/// <summary>
///     Generates a cluster-wide canary enforcer CronJob that continuously maintains
///     the desired canary traffic ratio across blue/green deployments. Reads the
///     <c>relay.calq.io/canary-weight</c> annotation from Services and scales
///     both slot deployments to maintain the replica ratio, compensating for HPA
///     scaling, pod crashes, and node preemption.
/// </summary>
public static class CanaryEnforcerGenerator {
    private const string ComponentName = "canary-enforcer";

    /// <summary>
    ///     Generates all Kubernetes manifests for the cluster-wide canary enforcer.
    /// </summary>
    /// <param name="ns">Namespace for the enforcer resources.</param>
    /// <param name="schedule">CronJob schedule expression.</param>
    /// <param name="kubectlImage">kubectl container image URL.</param>
    public static string Generate(string ns = "calq-relay-system", string schedule = "* * * * *", string kubectlImage = "bitnami/kubectl:latest") {
        string script = GetScript();

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
                  - apiGroups: [""]
                    resources: ["services"]
                    verbs: ["get", "list"]
                  - apiGroups: ["apps"]
                    resources: ["deployments"]
                    verbs: ["get", "list"]
                  - apiGroups: ["apps"]
                    resources: ["deployments/scale"]
                    verbs: ["get", "patch"]
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

    private static string GetScript() =>
        "                  # Iterate all non-system namespaces\n" + "                  for NS in $(kubectl get namespaces -o jsonpath='{.items[*].metadata.name}'); do\n" + "                    case \"$NS\" in kube-*|calq-relay-*) continue;; esac\n" +
        "                    \n" + "                    # Find Services with canary annotation\n" +
        "                    for SVC in $(kubectl get services -n \"$NS\" -o jsonpath='{range .items[*]}{.metadata.name}{\"=\"}{.metadata.annotations.relay\\.calq\\.io/canary-weight}{\"\\n\"}{end}' 2>/dev/null); do\n" +
        "                      SVC_NAME=$(echo \"$SVC\" | cut -d= -f1)\n" + "                      WEIGHT=$(echo \"$SVC\" | cut -d= -f2)\n" + "                      [ -z \"$WEIGHT\" ] && continue\n" +
        "                      [ \"$WEIGHT\" = \"<none>\" ] && continue\n" + "                      \n" + "                      # Read active slot from annotation\n" +
        "                      ACTIVE_SLOT=$(kubectl get service \"$SVC_NAME\" -n \"$NS\" -o jsonpath='{.metadata.annotations.relay\\.calq\\.io/active-slot}' 2>/dev/null)\n" + "                      [ -z \"$ACTIVE_SLOT\" ] && ACTIVE_SLOT=\"blue\"\n" +
        "                      if [ \"$ACTIVE_SLOT\" = \"blue\" ]; then INACTIVE_SLOT=\"green\"; else INACTIVE_SLOT=\"blue\"; fi\n" + "                      \n" + "                      # Get current replica counts\n" +
        "                      ACTIVE_REPLICAS=$(kubectl get deployment \"${SVC_NAME}-${ACTIVE_SLOT}\" -n \"$NS\" -o jsonpath='{.spec.replicas}' 2>/dev/null)\n" +
        "                      INACTIVE_REPLICAS=$(kubectl get deployment \"${SVC_NAME}-${INACTIVE_SLOT}\" -n \"$NS\" -o jsonpath='{.spec.replicas}' 2>/dev/null)\n" + "                      [ -z \"$ACTIVE_REPLICAS\" ] && continue\n" +
        "                      [ -z \"$INACTIVE_REPLICAS\" ] && continue\n" + "                      \n" + "                      # Calculate desired split\n" + "                      TOTAL=$((ACTIVE_REPLICAS + INACTIVE_REPLICAS))\n" +
        "                      [ \"$TOTAL\" -lt 2 ] && TOTAL=2\n" + "                      DESIRED_INACTIVE=$(( (TOTAL * WEIGHT + 50) / 100 ))\n" + "                      [ \"$DESIRED_INACTIVE\" -lt 1 ] && DESIRED_INACTIVE=1\n" +
        "                      [ \"$DESIRED_INACTIVE\" -ge \"$TOTAL\" ] && DESIRED_INACTIVE=$((TOTAL - 1))\n" + "                      DESIRED_ACTIVE=$((TOTAL - DESIRED_INACTIVE))\n" + "                      \n" +
        "                      # Scale if needed\n" + "                      if [ \"$ACTIVE_REPLICAS\" -ne \"$DESIRED_ACTIVE\" ] || [ \"$INACTIVE_REPLICAS\" -ne \"$DESIRED_INACTIVE\" ]; then\n" +
        "                        kubectl scale deployment \"${SVC_NAME}-${ACTIVE_SLOT}\" -n \"$NS\" --replicas=\"$DESIRED_ACTIVE\"\n" +
        "                        kubectl scale deployment \"${SVC_NAME}-${INACTIVE_SLOT}\" -n \"$NS\" --replicas=\"$DESIRED_INACTIVE\"\n" +
        "                        echo \"Enforced canary $NS/$SVC_NAME: ${DESIRED_ACTIVE} ($ACTIVE_SLOT) + ${DESIRED_INACTIVE} ($INACTIVE_SLOT) = ${WEIGHT}%\"\n" + "                      fi\n" + "                    done\n" + "                  done";
}
