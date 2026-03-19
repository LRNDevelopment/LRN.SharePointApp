using LRN.SharePointClient.Models;

public sealed class SynchronizerWorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 120;

    /// <summary>
    /// Uses the same UploadPaths array as the uploader, but this worker will only process
    /// items where IsSharePointUpload == false (SharePoint -> Server).
    /// </summary>
    public List<UploadPathItem> UploadPaths { get; set; } = new();
}
