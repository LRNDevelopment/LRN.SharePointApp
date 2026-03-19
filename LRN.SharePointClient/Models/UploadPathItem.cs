namespace LRN.SharePointClient.Models;

/// <summary>
/// One sync rule. When <see cref="IsSharePointUpload"/> is true: ServerOutputFolder -> SharePoint.
/// When false: SharePoint -> ServerOutputFolder.
/// </summary>
public sealed class UploadPathItem
{
    public bool IsSharePointUpload { get; set; }

    /// <summary>
    /// A business label for the sync (e.g. "Coding Validation Report").
    /// This will be written to LRN_STEP_LOG.SyncronizeFileType when enabled.
    /// </summary>
    public string SyncronizeFileType { get; set; } = "";

    /// <summary>Local server folder path.</summary>
    public string ServerOutputFolder { get; set; } = "";

    /// <summary>
    /// SharePoint folder path or SharePoint folder link (AllItems.aspx?id=...)
    /// </summary>
    public string SharePointFolder { get; set; } = "";

    /// <summary>Recursively include subfolders.</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>Overwrite existing files when syncing.</summary>
    public bool OverwriteExisting { get; set; } = false;
}
