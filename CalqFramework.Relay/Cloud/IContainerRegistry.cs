namespace CalqFramework.Relay.Cloud;

/// <summary>
///     Manages container image operations against a cloud registry.
/// </summary>
public interface IContainerRegistry {
    string LoginServer { get; }
    void Authenticate();
    string GetImageUrl(string imageName, string tag);
    bool ImageExists(string imageRef);
    void ImportImage(string sourceImageUrl, IContainerRegistry? sourceRegistry = null);
}
