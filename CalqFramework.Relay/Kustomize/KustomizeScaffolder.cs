namespace CalqFramework.Relay.Kustomize;

/// <summary>
///     Generates Kustomize directory structures for services.
///     Non-blue-green: flat directory with all manifests.
///     Blue-green: base (shared Service/ConfigMap) + blue/green (slot deployments).
/// </summary>
public static class KustomizeScaffolder {
    /// <param name="basePath">Parent directory (e.g., "k8s").</param>
    /// <param name="serviceName">Service name (becomes subdirectory).</param>
    /// <param name="blueGreen">Generate blue/green slot directories with shared base.</param>
    /// <param name="expose">Exposure mode: none, internal, public, or ingress.</param>
    /// <param name="domain">Domain for ingress (e.g., myapp.example.com).</param>
    /// <param name="clusterProvider">Cloud provider for LoadBalancer annotations.</param>
    /// <param name="port">Container port. Default: 8080.</param>
    public static void Scaffold(string basePath, string serviceName, bool blueGreen, string expose = "none", string domain = "", string clusterProvider = "", int port = 8080) {
        string servicePath = Path.Combine(basePath, serviceName);
        bool hasIngress = expose == "ingress" && !string.IsNullOrEmpty(domain);

        if (blueGreen) {
            ScaffoldBlueGreen(servicePath, serviceName, expose, domain, clusterProvider, hasIngress, port);
        } else {
            ScaffoldSimple(servicePath, serviceName, expose, domain, clusterProvider, hasIngress, port);
        }
    }

    private static void ScaffoldSimple(string servicePath, string serviceName, string expose, string domain, string clusterProvider, bool hasIngress, int port) {
        Directory.CreateDirectory(servicePath);

        string resources = "  - deployment.yaml\n  - service.yaml\n  - configmap.yaml";
        if (hasIngress) {
            resources += "\n  - ingress.yaml";
        }

        File.WriteAllText(Path.Combine(servicePath, "kustomization.yaml"), $"apiVersion: kustomize.config.k8s.io/v1beta1\nkind: Kustomization\nresources:\n{resources}\n");

        File.WriteAllText(Path.Combine(servicePath, "deployment.yaml"), GenerateDeployment(serviceName, serviceName, port: port));
        File.WriteAllText(Path.Combine(servicePath, "service.yaml"), GenerateService(serviceName, "app", serviceName, expose, clusterProvider, domain, port));
        File.WriteAllText(Path.Combine(servicePath, "configmap.yaml"), GenerateConfigMap(serviceName));
        if (hasIngress) {
            File.WriteAllText(Path.Combine(servicePath, "ingress.yaml"), GenerateIngress(serviceName, domain, port, clusterProvider));
        }
    }

    private static void ScaffoldBlueGreen(string servicePath, string serviceName, string expose, string domain, string clusterProvider, bool hasIngress, int port) {
        string baseDir = Path.Combine(servicePath, "base");
        Directory.CreateDirectory(baseDir);

        string resources = "  - service.yaml\n  - configmap.yaml";
        if (hasIngress) {
            resources += "\n  - ingress.yaml";
        }

        File.WriteAllText(Path.Combine(baseDir, "kustomization.yaml"), $"apiVersion: kustomize.config.k8s.io/v1beta1\nkind: Kustomization\nresources:\n{resources}\n");

        File.WriteAllText(Path.Combine(baseDir, "service.yaml"), GenerateService(serviceName, "slot", "blue", expose, clusterProvider, domain, port));
        File.WriteAllText(Path.Combine(baseDir, "configmap.yaml"), GenerateConfigMap(serviceName));
        if (hasIngress) {
            File.WriteAllText(Path.Combine(baseDir, "ingress.yaml"), GenerateIngress(serviceName, domain, port, clusterProvider));
        }

        ScaffoldSlot(servicePath, serviceName, "blue", port);
        ScaffoldSlot(servicePath, serviceName, "green", port);
    }

    private static void ScaffoldSlot(string servicePath, string serviceName, string slot, int port) {
        string slotDir = Path.Combine(servicePath, slot);
        Directory.CreateDirectory(slotDir);

        File.WriteAllText(Path.Combine(slotDir, "deployment.yaml"), GenerateDeployment($"{serviceName}-{slot}", serviceName, slot, port));

        File.WriteAllText(Path.Combine(slotDir, "kustomization.yaml"), "apiVersion: kustomize.config.k8s.io/v1beta1\n" + "kind: Kustomization\n" + "resources:\n" + "  - ../base\n" + "  - deployment.yaml\n");
    }

    private static string GenerateDeployment(string deploymentName, string serviceName, string slot = "", int port = 8080) {
        string slotLabel = string.IsNullOrEmpty(slot) ? "" : $"\n        slot: \"{slot}\"";
        return "apiVersion: apps/v1\n" + "kind: Deployment\n" + "metadata:\n" + $"  name: {deploymentName}\n" + "spec:\n" + "  selector:\n" + "    matchLabels:\n" + $"      app: {serviceName}{slotLabel}\n" + "  strategy:\n" + "    type: RollingUpdate\n" +
               "    rollingUpdate:\n" + "      maxUnavailable: 0\n" + "      maxSurge: 100%\n" + "  template:\n" + "    metadata:\n" + "      labels:\n" + $"        app: {serviceName}{slotLabel}\n" + "    spec:\n" + "      volumes:\n" +
               "        - name: config\n" + "          configMap:\n" + $"            name: {serviceName}\n" + "      containers:\n" + $"        - name: {serviceName}\n" + $"          image: {serviceName}\n" + "          ports:\n" +
               $"            - containerPort: {port}\n" + "          volumeMounts:\n" + "            - name: config\n" + "              mountPath: /app/config\n" + "              readOnly: true\n";
    }

    private static string GenerateService(string serviceName, string selectorKey, string selectorValue, string expose, string clusterProvider, string domain = "", int port = 8080) {
        var annotations = new List<string>();

        if (expose == "internal") {
            switch (clusterProvider.ToLowerInvariant()) {
                case "azure" or "aks": annotations.Add("    service.beta.kubernetes.io/azure-load-balancer-internal: \"true\""); break;
                case "gcp" or "gke": annotations.Add("    networking.gke.io/load-balancer-type: Internal"); break;
            }
        }

        if (expose == "public" && clusterProvider.ToLowerInvariant() is "azure" or "aks") {
            annotations.Add($"    service.beta.kubernetes.io/azure-dns-label-name: {serviceName}");
        }

        if (!string.IsNullOrEmpty(domain) && expose is "internal" or "public") {
            annotations.Add($"    external-dns.alpha.kubernetes.io/hostname: {domain}");
        }

        string serviceType = expose is "internal" or "public" ? "LoadBalancer" : "ClusterIP";
        string annotationsBlock = annotations.Count > 0 ? $"\n  annotations:\n{string.Join("\n", annotations)}" : "";
        string sessionAffinity = expose == "public" ? "ClientIP" : "None";

        return "apiVersion: v1\n" + "kind: Service\n" + "metadata:\n" + $"  name: {serviceName}{annotationsBlock}\n" + "spec:\n" + $"  type: {serviceType}\n" + $"  sessionAffinity: {sessionAffinity}\n" + "  selector:\n" +
               $"    {selectorKey}: {selectorValue}\n" + "  ports:\n" + "    - name: http\n" + "      port: 80\n" + $"      targetPort: {port}\n";
    }

    private static string GenerateConfigMap(string serviceName) =>
        "apiVersion: v1\n" + "kind: ConfigMap\n" + "metadata:\n" + $"  name: {serviceName}\n" + "data:\n" + "  appsettings.k8s.json: |\n" + "    {\n" + "    }\n";

    private static string GenerateIngress(string serviceName, string domain, int port, string clusterProvider = "") {
        string ingressClass = clusterProvider.ToLowerInvariant() switch {
            "gcp" or "gke" => "  ingressClassName: gce\n",
            _ => ""
        };
        return "apiVersion: networking.k8s.io/v1\n" + "kind: Ingress\n" + "metadata:\n" + $"  name: {serviceName}\n" + "  annotations:\n" + "    cert-manager.io/cluster-issuer: letsencrypt-prod\n" +
               $"    external-dns.alpha.kubernetes.io/hostname: {domain}\n" + "spec:\n" + ingressClass + "  tls:\n" + "    - hosts:\n" + $"        - {domain}\n" + $"      secretName: {serviceName}-tls\n" + "  rules:\n" + $"    - host: {domain}\n" +
               "      http:\n" + "        paths:\n" + "          - path: /\n" + "            pathType: Prefix\n" + "            backend:\n" + "              service:\n" + $"                name: {serviceName}\n" + "                port:\n" +
               $"                  number: {port}\n";
    }
}
