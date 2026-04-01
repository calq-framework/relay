namespace CalqFramework.Relay.Docker;

/// <summary>
///     Generates Dockerfiles for .NET projects by detecting the target framework
///     and project type from the project file (.csproj, .fsproj, .vbproj).
/// </summary>
public static class DockerfileGenerator {
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    /// <summary>
    ///     Generates a multi-stage Dockerfile for a .NET project.
    ///     Detects the target framework version and assembly name from the project file.
    /// </summary>
    public static string Generate(string csprojPath) {
        XDocument doc = XDocument.Load(csprojPath);
        XElement? props = doc.Root?.Element("PropertyGroup");

        string tfm = props?.Element("TargetFramework")
            ?.Value ?? "net9.0";
        string version = tfm.Replace("net", "");

        string assemblyName = props?.Element("AssemblyName")
            ?.Value ?? Path.GetFileNameWithoutExtension(csprojPath);

        string baseImage = tfm.Contains("aspnet") || HasWebSdk(doc) ? $"mcr.microsoft.com/dotnet/aspnet:{version}" : $"mcr.microsoft.com/dotnet/runtime:{version}";

        string sdkImage = $"mcr.microsoft.com/dotnet/sdk:{version}";
        string csprojFile = Path.GetFileName(csprojPath);
        string projectDir = Path.GetDirectoryName(csprojPath) ?? ".";
        string relativeDir = Path.GetRelativePath(".", projectDir)
            .Replace("\\", "/");
        if (relativeDir == ".") {
            relativeDir = "";
        }

        _ = string.IsNullOrEmpty(relativeDir) ? "." : relativeDir;
        string publishPath = string.IsNullOrEmpty(relativeDir) ? $"\"{csprojFile}\"" : $"\"{relativeDir}/{csprojFile}\"";

        return $$"""
                 FROM {{sdkImage}} AS build
                 WORKDIR /src
                 COPY . .
                 RUN dotnet publish {{publishPath}} -c Release -o /app/publish --self-contained false

                 FROM {{baseImage}} AS final
                 WORKDIR /app
                 EXPOSE 80
                 COPY --from=build /app/publish .
                 ENTRYPOINT ["dotnet", "{{assemblyName}}.dll"]
                 """;
    }

    /// <summary>
    ///     Finds the first .NET project file in the directory tree that looks like
    ///     a web/API project (has Web SDK, or references ASP.NET packages).
    /// </summary>
    public static string? FindWebProject(string searchDir) {
        IEnumerable<string> allProjects = ProjectPatterns.SelectMany(p => Directory.EnumerateFiles(searchDir, p, SearchOption.AllDirectories))
            .Where(f => !f.Contains("Test", StringComparison.OrdinalIgnoreCase));

        foreach (string file in allProjects) {
            try {
                XDocument doc = XDocument.Load(file);
                if (HasWebSdk(doc) || HasAspNetReference(doc)) {
                    return file;
                }
            } catch {
            }
        }

        return allProjects.FirstOrDefault();
    }

    private static bool HasWebSdk(XDocument doc) {
        string? sdk = doc.Root?.Attribute("Sdk")
            ?.Value ?? "";
        return sdk.Contains("Web", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAspNetReference(XDocument doc) {
        IEnumerable<XElement> refs = doc.Descendants("PackageReference");
        return refs.Any(r => r.Attribute("Include")
            ?.Value.Contains("AspNet", StringComparison.OrdinalIgnoreCase) == true);
    }
}
