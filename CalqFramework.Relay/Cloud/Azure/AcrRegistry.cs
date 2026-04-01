namespace CalqFramework.Relay.Cloud.Azure;

public class AcrRegistry(string name) : IContainerRegistry {
    private readonly string _name = name;

    public AcrRegistry(RegistryConfig config) : this(config.Name) { }

    public string LoginServer => $"{_name}.azurecr.io";

    public void Authenticate() {
        string token = CMD($"az acr login --name {_name} --output json --expose-token --only-show-errors")
            .Trim();
        int start = token.IndexOf("\"accessToken\":\"") + "\"accessToken\":\"".Length;
        int end = token.IndexOf("\"", start);
        string accessToken = token[start..end];
        RUN($"docker login {LoginServer} -u 00000000-0000-0000-0000-000000000000 -p {accessToken}");
    }

    public string GetImageUrl(string imageName, string tag) => $"{LoginServer}/{imageName}:{tag}";

    public bool ImageExists(string imageRef) {
        try {
            RUN($"az acr repository show --name {_name} --image {imageRef} --only-show-errors");
            return true;
        } catch {
            return false;
        }
    }

    public void ImportImage(string sourceImageUrl, IContainerRegistry? sourceRegistry = null) {
        string imageRef = sourceImageUrl.Contains('/') ? sourceImageUrl[(sourceImageUrl.IndexOf('/') + 1)..] : sourceImageUrl;
        if (ImageExists(imageRef)) {
            return;
        }

        if (sourceRegistry is AcrRegistry) {
            RUN($"az acr import --name {_name} --source {sourceImageUrl} --only-show-errors");
        } else {
            RUN($"docker pull {sourceImageUrl}");
            string targetUrl = $"{LoginServer}/{imageRef}";
            RUN($"docker tag {sourceImageUrl} {targetUrl}");
            RUN($"docker push {targetUrl}");
        }
    }
}
