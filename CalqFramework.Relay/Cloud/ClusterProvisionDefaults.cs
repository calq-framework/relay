namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Embedded default provisioning steps for Azure and GCP.
///     Used when no config file exists locally or in the org repo.
/// </summary>
public static class ClusterProvisionDefaults {
    public static ClusterProvisionConfig Gcp() => new() {
        Steps = [
            new() {
                Command = "gcloud projects create {project}",
                ContinueOnError = true
            },
            new() {
                Command = "gcloud billing projects link {project} --billing-account {billingAccount}",
                ContinueOnError = true
            },
            new() {
                Command = "gcloud services enable container.googleapis.com artifactregistry.googleapis.com dns.googleapis.com --project {project}"
            },
            new() {
                Command = "gcloud artifacts repositories create {registry} --repository-format docker --location {region} --project {project}",
                ContinueOnError = true
            },
            new() {
                Command = "gcloud container clusters create {cluster} --region {region} --project {project} --num-nodes 1 --enable-autoscaling --min-nodes {minNodes} --max-nodes {maxNodes} --workload-pool {project}.svc.id.goog --release-channel regular",
                ContinueOnError = true
            }
        ],
        DestroySteps = [
            new() {
                Command = "gcloud container clusters delete {cluster} --region {region} --project {project} --quiet --async"
            }
        ],
        PostSteps = [
            new() {
                Command = "gcloud artifacts repositories add-iam-policy-binding {registry} --location {region} --project {project} --member serviceAccount:{computeSa} --role roles/artifactregistry.reader",
                ContinueOnError = true
            },
            new() {
                Command = "helm repo add jetstack https://charts.jetstack.io --force-update"
            },
            new() {
                Command = "helm upgrade --install cert-manager jetstack/cert-manager --namespace cert-manager --create-namespace --set crds.enabled=true --wait --timeout 5m"
            },
            new() {
                Command = "helm repo add argo https://argoproj.github.io/argo-helm"
            },
            new() {
                Command = "helm repo update argo"
            },
            new() {
                Command = "helm upgrade --install argocd argo/argo-cd --namespace argocd --create-namespace --set server.extraArgs[0]=--insecure --set configs.params.application\\.resourceTrackingMethod=annotation --wait --timeout 5m"
            }
        ]
    };

    public static ClusterProvisionConfig Azure() => new() {
        Steps = [
            new() {
                Command = "az group create --name {resourceGroup} --location {region}",
                ContinueOnError = true
            },
            new() {
                Command = "az acr create --name {registry} --resource-group {resourceGroup} --sku Standard",
                ContinueOnError = true
            },
            new() {
                Command =
                    "az aks create --name {cluster} --resource-group {resourceGroup} --node-count 1 --enable-managed-identity --enable-cluster-autoscaler --min-count {minNodes} --max-count {maxNodes} --network-plugin azure --attach-acr {registry} --generate-ssh-keys --yes",
                ContinueOnError = true
            }
        ],
        DestroySteps = [
            new() {
                Command = "az aks delete --name {cluster} --resource-group {resourceGroup} --yes --no-wait"
            }
        ],
        PostSteps = [
            new() {
                Command = "helm repo add jetstack https://charts.jetstack.io --force-update"
            },
            new() {
                Command = "helm upgrade --install cert-manager jetstack/cert-manager --namespace cert-manager --create-namespace --set crds.enabled=true --wait --timeout 5m"
            },
            new() {
                Command = "helm repo add argo https://argoproj.github.io/argo-helm"
            },
            new() {
                Command = "helm repo update argo"
            },
            new() {
                Command = "helm upgrade --install argocd argo/argo-cd --namespace argocd --create-namespace --set server.extraArgs[0]=--insecure --set configs.params.application\\.resourceTrackingMethod=annotation --wait --timeout 5m"
            }
        ]
    };
}
