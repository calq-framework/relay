namespace CalqFramework.Relay.Cloud.Azure;

public class AksAuthenticator : IClusterAuthenticator {
    public void Authenticate(ClusterConfig cluster) => RUN($"az aks get-credentials --resource-group {cluster.ResourceGroup} --name {cluster.Name} --overwrite-existing");

    public string GetContextName(ClusterConfig cluster) => cluster.Name;
}
