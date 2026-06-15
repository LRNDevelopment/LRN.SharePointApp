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

    /// <summary>
    /// When downloading, keep the configured SharePoint folder path under the local output folder.
    /// For dated report folders, this preserves Year / Month / Week locally.
    /// </summary>
    public bool PreserveSharePointFolderStructure { get; set; } = false;

    /// <summary>
    /// Optional file name filter used by SharePoint -> Server sync.
    /// Example: Certus_Merged Billing*.xlsx
    /// </summary>
    public string? FileNamePattern { get; set; }

    /// <summary>
    /// When true, the synchronizer finds the most recent available Year / Month / Week folder
    /// under the configured SharePoint folder and recursively downloads missing files from it.
    /// </summary>
    public bool SyncLatestWeekOnly { get; set; } = false;

    /// <summary>
    /// When true, the synchronizer does not download the whole SharePoint tree.
    /// It finds the latest Year / Month / Week folder, then downloads files from the raw-file folder only.
    /// Also enabled automatically when SyncronizeFileType contains Raw.
    /// </summary>
    public bool SyncLatestWeekRawOnly { get; set; } = false;

    /// <summary>
    /// Optional raw folder name under the latest week folder.
    /// Example: Certus Master Raw Data. If empty, the worker auto-detects a folder containing Raw.
    /// </summary>
    public string? RawFolderName { get; set; }
}
