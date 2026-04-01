namespace CalqFramework.Relay;

/// <summary>
///     Result metadata returned by relay subcommands.
///     Serialized to JSON for machine-readable output.
/// </summary>
public class RelayResult {
    public string Service { get; set; } = "";
    public string Operation { get; set; } = "";
    public string SourceEnvironment { get; set; } = "";
    public string TargetEnvironment { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string SyncStatus { get; set; } = "";
    public bool DryRun { get; set; }
}
