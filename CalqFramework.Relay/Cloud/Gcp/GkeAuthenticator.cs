namespace CalqFramework.Relay.Cloud.Gcp;

public class GkeAuthenticator : IClusterAuthenticator {
    public void Authenticate(ClusterConfig cluster) => RUN($"gcloud container clusters get-credentials {cluster.Name} --region {cluster.Region} --project {cluster.Project}");

    public string GetContextName(ClusterConfig cluster) =>
        $"gke_{cluster.Project}_{cluster.Region}_{cluster.Name}";
}
