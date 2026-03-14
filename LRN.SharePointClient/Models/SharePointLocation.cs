namespace LRN.SharePointClient.Models;

/// <summary>
/// Simple location descriptor used by older helper services.
/// In the current implementation, uploader/synchronizer work directly with driveId + folderPath.
/// </summary>
public sealed class SharePointLocation
{
    public string DriveId { get; set; } = "";
    public string FolderPath { get; set; } = "";
}
