namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Provisions Kubernetes clusters, container registries, and DNS zones
///     on Azure and GCP with production-ready defaults.
/// </summary>
public static class ClusterProvisioner {
    /// <summary>
    ///     Creates a complete environment: cluster, registry, and optionally DNS zone
    ///     with ExternalDNS and cert-manager installed.
    /// </summary>
    public static void Provision(ClusterConfig cluster, RegistryConfig registry, string domain = "") {
        switch (cluster.Provider.ToLowerInvariant()) {
            case "azure" or "aks":
                ProvisionAzure(cluster, registry, domain);
                break;
            case "gcp" or "gke":
                ProvisionGcp(cluster, registry, domain);
                break;
            default:
                throw new InvalidOperationException($"Cluster provisioning is not supported for provider '{cluster.Provider}'. Provision the cluster manually, then use add-service/add-environment to register it.");
        }

        // Install cluster add-ons
        AuthenticateCluster(cluster);
        InstallCertManager();
        if (!string.IsNullOrEmpty(domain)) {
            InstallExternalDns(cluster.Provider, cluster, domain);
        }
    }

    /// <summary>
    ///     Destroys a cluster and its associated resources.
    /// </summary>
    public static void Destroy(ClusterConfig cluster, RegistryConfig? registry) {
        switch (cluster.Provider.ToLowerInvariant()) {
            case "azure" or "aks":
                RUN($"az aks delete --name {cluster.Name} --resource-group {cluster.ResourceGroup} --yes --no-wait");
                if (registry != null) {
                    RUN($"az acr delete --name {registry.Name} --resource-group {cluster.ResourceGroup} --yes");
                }

                Console.Error.WriteLine($"Deleting {cluster.Name}{(registry != null ? $" and {registry.Name}" : "")} (async)");
                break;
            case "gcp" or "gke":
                RUN($"gcloud container clusters delete {cluster.Name} --region {cluster.Region} --project {cluster.Project} --quiet --async");
                if (registry != null) {
                    RUN($"gcloud artifacts repositories delete {registry.Name} --location {registry.Region} --project {registry.Project} --quiet");
                }

                Console.Error.WriteLine($"Deleting {cluster.Name}{(registry != null ? $" and {registry.Name}" : "")} (async)");
                break;
        }
    }

    private static void ProvisionAzure(ClusterConfig cluster, RegistryConfig registry, string domain) {
        string location = !string.IsNullOrEmpty(cluster.Region) ? cluster.Region : "eastus";

        // Resource group
        RUN($"az group create --name {cluster.ResourceGroup} --location {location}");

        // Container registry
        try {
            RUN($"az acr create --name {registry.Name} --resource-group {cluster.ResourceGroup} --sku Standard");
        } catch {
            Console.Error.WriteLine($"Registry '{registry.Name}' already exists, continuing.");
        }

        // AKS cluster with managed identity, autoscaling, and ACR integration
        try {
            RUN(
                "az aks create " + $"--name {cluster.Name} " + $"--resource-group {cluster.ResourceGroup} " + "--node-count 1 " + "--enable-managed-identity " + "--enable-cluster-autoscaler " + "--min-count 1 --max-count 1 " + "--network-plugin azure " +
                $"--attach-acr {registry.Name} " + "--generate-ssh-keys " + "--yes");
        } catch {
            Console.Error.WriteLine($"Cluster '{cluster.Name}' already exists, continuing.");
        }

        // DNS zone
        if (!string.IsNullOrEmpty(domain)) {
            RUN($"az network dns zone create --name {domain} --resource-group {cluster.ResourceGroup}");
            Console.Error.WriteLine($"Created Azure DNS zone: {domain}");
            Console.Error.WriteLine("Point your domain's NS records to the Azure nameservers shown above.");
        }
    }

    private static void ProvisionGcp(ClusterConfig cluster, RegistryConfig registry, string domain) {
        // Create project if it doesn't exist
        try {
            CMD($"gcloud projects describe {cluster.Project}");
        } catch {
            Console.Error.WriteLine($"Project '{cluster.Project}' not found, creating...");
            RUN($"gcloud projects create {cluster.Project}");

            // Link billing account (use the first available)
            try {
                string billingAccount = CMD("gcloud billing accounts list --format='value(ACCOUNT_ID)' --filter=open=true")
                    .Trim()
                    .Split('\n')[0]
                    .Trim();
                if (!string.IsNullOrEmpty(billingAccount)) {
                    RUN($"gcloud billing projects link {cluster.Project} --billing-account {billingAccount}");
                    Console.Error.WriteLine($"Linked billing account {billingAccount}");
                } else {
                    Console.Error.WriteLine("Warning: No billing account found. Enable billing manually before creating resources.");
                }
            } catch {
                Console.Error.WriteLine("Warning: Could not link billing account. Enable billing manually.");
            }
        }

        // Ensure APIs are enabled
        RUN($"gcloud services enable container.googleapis.com artifactregistry.googleapis.com dns.googleapis.com --project {cluster.Project}");

        // Artifact Registry
        try {
            RUN($"gcloud artifacts repositories create {registry.Name} " + "--repository-format docker " + $"--location {registry.Region} " + $"--project {registry.Project}");
        } catch {
            Console.Error.WriteLine($"Registry '{registry.Name}' already exists, continuing.");
        }

        // GKE cluster with autoscaling and workload identity
        try {
            RUN(
                $"gcloud container clusters create {cluster.Name} " + $"--region {cluster.Region} " + $"--project {cluster.Project} " + "--num-nodes 1 " + "--enable-autoscaling --min-nodes 1 --max-nodes 1 " +
                $"--workload-pool {cluster.Project}.svc.id.goog " + "--release-channel regular");
        } catch {
            Console.Error.WriteLine($"Cluster '{cluster.Name}' already exists, continuing.");
        }

        // Grant GKE nodes access to Artifact Registry
        try {
            string projectNumber = CMD($"gcloud projects describe {cluster.Project} --format=value(projectNumber)")
                .Trim();
            string gkeSa = $"{projectNumber}-compute@developer.gserviceaccount.com";
            RUN($"gcloud artifacts repositories add-iam-policy-binding {registry.Name} " + $"--location {registry.Region} " + $"--project {registry.Project} " + $"--member serviceAccount:{gkeSa} " + "--role roles/artifactregistry.reader");
            Console.Error.WriteLine("Granted GKE nodes access to Artifact Registry");
        } catch {
            Console.Error.WriteLine("Warning: Could not grant registry access. You may need to configure it manually.");
        }

        // GCP DNS zone
        if (!string.IsNullOrEmpty(domain)) {
            RUN($"gcloud dns managed-zones create {domain.Replace('.', '-')} " + $"--dns-name {domain}. " + "--description \"Managed by Calq Relay\" " + $"--project {cluster.Project}");
            Console.Error.WriteLine($"Created Cloud DNS zone: {domain}");
            Console.Error.WriteLine("Point your domain's NS records to the Google nameservers shown above.");
        }
    }

    private static void AuthenticateCluster(ClusterConfig cluster) => CloudFactory.CreateAuthenticator(cluster.Provider)
        .Authenticate(cluster);

    private static void InstallCertManager() {
        RUN("helm repo add jetstack https://charts.jetstack.io --force-update");
        RUN("helm upgrade --install cert-manager jetstack/cert-manager " + "--namespace cert-manager --create-namespace " + "--set crds.enabled=true " + "--wait --timeout 5m");

        // Create a default ClusterIssuer for Let's Encrypt
        string issuer = "apiVersion: cert-manager.io/v1\n" + "kind: ClusterIssuer\n" + "metadata:\n" + "  name: letsencrypt-prod\n" + "spec:\n" + "  acme:\n" + "    server: https://acme-v02.api.letsencrypt.org/directory\n" + "    privateKeySecretRef:\n" +
                        "      name: letsencrypt-prod\n" + "    solvers:\n" + "      - http01:\n" + "          ingress:\n" + "            class: nginx\n";
        string tmpFile = Path.GetTempFileName();
        File.WriteAllText(tmpFile, issuer);
        RUN($"kubectl apply -f {tmpFile}");
        File.Delete(tmpFile);
        Console.Error.WriteLine("Installed cert-manager with Let's Encrypt ClusterIssuer");
    }

    private static void InstallExternalDns(string provider, ClusterConfig cluster, string domain) {
        RUN("helm repo add external-dns https://kubernetes-sigs.github.io/external-dns --force-update");

        string providerArgs = provider.ToLowerInvariant() switch {
            "azure" or "aks" => $"--set provider.name=azure --set azure.resourceGroup={cluster.ResourceGroup}",
            "gcp" or "gke" => $"--set provider.name=google --set google.project={cluster.Project}",
            _ => ""
        };

        RUN("helm upgrade --install external-dns external-dns/external-dns " + "--namespace external-dns --create-namespace " + $"{providerArgs} " + $"--set domainFilters[0]={domain} " + "--set policy=sync " + "--wait --timeout 5m");

        Console.Error.WriteLine($"Installed ExternalDNS for {domain}");
    }
}
