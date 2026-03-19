using LRN.SharePointClient.Models;

public sealed class UploaderWorkerOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Polling interval. Set to 0 to run once and stop the service.</summary>
    public int PollSeconds { get; set; } = 300;

    /// <summary>Rules (loaded from root UploadPaths array for backward compatibility).</summary>
    public List<UploadPathItem> UploadPaths { get; set; } = new();
}
