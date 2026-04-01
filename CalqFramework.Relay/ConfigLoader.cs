namespace CalqFramework.Relay;

/// <summary>
///     Loads platform configuration from YAML or JSON files.
/// </summary>
public static class ConfigLoader {
    public static PlatformConfig Load(string path) {
        string content = File.ReadAllText(path);
        string ext = Path.GetExtension(path)
            .ToLowerInvariant();

        if (ext is ".json") {
            return JsonSerializer.Deserialize<PlatformConfig>(
                content,
                new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                }) ?? new PlatformConfig();
        }

        // For YAML, shell out to yq to convert to JSON then deserialize
        try {
            string json = CMD($"yq -o json \"{path}\"");
            return JsonSerializer.Deserialize<PlatformConfig>(
                json,
                new JsonSerializerOptions {
                    PropertyNameCaseInsensitive = true
                }) ?? new PlatformConfig();
        } catch {
            throw new InvalidOperationException($"Failed to parse config file: {path}. Install yq for YAML support or use JSON format.");
        }
    }
}
