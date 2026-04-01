namespace CalqFramework.Relay.Cloud.Gcp;

public class GarRegistry(string project, string region, string repository) : IContainerRegistry {
    private readonly string _project = project;
    private readonly string _region = region;
    private readonly string _repository = repository;

    public GarRegistry(RegistryConfig config) : this(config.Project, config.Region, config.Name) { }

    public string LoginServer => $"{_region}-docker.pkg.dev/{_project}/{_repository}";

    public void Authenticate() => RUN($"gcloud auth configure-docker {_region}-docker.pkg.dev --quiet");

    public string GetImageUrl(string imageName, string tag) => $"{LoginServer}/{imageName}:{tag}";

    public bool ImageExists(string imageRef) {
        try {
            string fullRef = imageRef.Contains('/') ? imageRef : $"{LoginServer}/{imageRef}";
            RUN($"gcloud artifacts docker images describe {fullRef} --project {_project} --quiet");
            return true;
        } catch {
            return false;
        }
    }

    public void ImportImage(string sourceImageUrl, IContainerRegistry? sourceRegistry = null) {
        string imageRef = sourceImageUrl.Contains('/') ? sourceImageUrl[(sourceImageUrl.LastIndexOf('/') + 1)..] : sourceImageUrl;
        if (ImageExists(imageRef)) {
            return;
        }

        if (sourceRegistry is GarRegistry) {
            try {
                RUN($"gcrane copy {sourceImageUrl} {LoginServer}/{imageRef}");
                return;
            } catch {
            }
        }

        RUN($"docker pull {sourceImageUrl}");
        string target = $"{LoginServer}/{imageRef}";
        RUN($"docker tag {sourceImageUrl} {target}");
        RUN($"docker push {target}");
    }
}
