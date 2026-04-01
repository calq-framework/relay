namespace CalqFramework.Relay.Kubernetes;

/// <summary>
///     Direct kubectl operations for imperative commands that ArgoCD cannot handle.
/// </summary>
public static class KubeOps {
    public static int GetReplicaCount(string deploymentName, string ns) {
        string result = CMD($"kubectl get deployment {deploymentName} -n {ns} -o jsonpath=\"{{.spec.replicas}}\"")
            .Trim();
        return int.TryParse(result, out int count) ? count : 1;
    }

    public static string GetDeploymentImage(string deploymentName, string ns) =>
        CMD($"kubectl get deployment {deploymentName} -n {ns} -o jsonpath=\"{{.spec.template.spec.containers[0].image}}\"")
            .Trim();

    public static string GetDeploymentBySelector(string selector, string ns) =>
        CMD($"kubectl get deployments -n {ns} --selector={selector} -o jsonpath=\"{{.items[0].metadata.name}}\"")
            .Trim();

    public static void RestartParallel(string deploymentName, string ns, string appName = "") {
        if (string.IsNullOrEmpty(appName)) {
            appName = deploymentName;
        }

        string ts = DateTime.UtcNow.ToString("yyyy-MM-ddtHHmmss");
        string patch = "{\"spec\":{\"template\":{\"metadata\":{\"labels\":{\"version\":\"" + ts + "\"}},\"spec\":{\"affinity\":{\"podAntiAffinity\":{\"requiredDuringSchedulingIgnoredDuringExecution\":" + "[{\"labelSelector\":{\"matchExpressions\":[" +
                       "{\"key\":\"app\",\"operator\":\"In\",\"values\":[\"" + appName + "\"]}," + "{\"key\":\"version\",\"operator\":\"In\",\"values\":[\"" + ts + "\"]}]},\"topologyKey\":\"kubernetes.io/hostname\"}]}}}}}}";
        RUN($"kubectl patch deployment {deploymentName} -n {ns} -p '{patch}'");
    }

    public static void RestartSequential(string deploymentName, string ns) => RUN($"kubectl rollout restart deployment/{deploymentName} -n {ns}");

    public static void UseContext(string contextName) => RUN($"kubectl config use-context {contextName}");

    public static void SetNamespace(string ns) => RUN($"kubectl config set-context --current --namespace={ns}");

    public static string ExportResources(string ns, string selector) {
        string resources = CMD("kubectl api-resources --verbs=list --namespaced -o name")
            .Trim();
        string output = "";
        foreach (string resource in resources.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            string r = resource.Trim();
            if (r is "events" or "events.events.k8s.io" or "jobs.batch" or "endpoints" or "endpointslices.discovery.k8s.io") {
                continue;
            }

            try {
                string yaml = CMD($"kubectl get {r} --ignore-not-found -n {ns} --selector={selector} -o yaml");
                if (!string.IsNullOrWhiteSpace(yaml) && yaml.Contains("items:")) {
                    output += $"---\n{yaml}\n";
                }
            } catch {
            }
        }

        return output;
    }
}
