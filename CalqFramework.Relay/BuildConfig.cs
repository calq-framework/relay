namespace CalqFramework.Relay;

/// <summary>
///     Build pipeline configuration. Controls how services are built into
///     container images. Defaults to .NET auto-detection; override fields
///     to support any language or build system.
/// </summary>
public class BuildConfig {
    /// <summary>
    ///     Path to an existing Dockerfile. When set, skips auto-generation.
    ///     Relative to the repository root.
    /// </summary>
    public string Dockerfile { get; set; } = "";

    /// <summary>
    ///     Shell command to build the container image.
    ///     Placeholders: {dockerfile}, {image}, {context}.
    ///     Default: "docker build -f {dockerfile} -t {image} {context}"
    /// </summary>
    public string BuildCommand { get; set; } = "docker build -f {dockerfile} -t {image} {context}";

    /// <summary>
    ///     Shell command to push the container image.
    ///     Placeholder: {image}.
    ///     Default: "docker push {image}"
    /// </summary>
    public string PushCommand { get; set; } = "docker push {image}";

    /// <summary>
    ///     Docker build context directory. Relative to the repository root.
    ///     Default: "." (repository root).
    /// </summary>
    public string Context { get; set; } = ".";

    /// <summary>
    ///     Image tag template. Supports placeholder: {sha} (12-char Git commit SHA).
    ///     Default: "{sha}"
    /// </summary>
    public string Tag { get; set; } = "{sha}";
}
