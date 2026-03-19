using LRN.SharePointClient.Models;

namespace LRN.SharePointClient.Abstractions;

/// <summary>
/// Minimal SharePoint client abstraction used by the uploader/synchronizer workers.
/// Implementation uses Microsoft Graph (app-only).
/// </summary>
public interface ISharePointClient
{
    /// <summary>Resolves the configured drive id (Document Library) for the configured site.</summary>
    Task<string?> TryResolveDriveIdAsync(CancellationToken ct);

    /// <summary>Ensures a folder path exists under the drive root (creates missing folders).</summary>
    Task EnsureFolderPathAsync(string driveId, string folderPath, CancellationToken ct);

    /// <summary>Returns a drive item for a path, or null if it does not exist.</summary>
    Task<SharePointItem?> TryGetItemByPathAsync(string driveId, string itemPath, CancellationToken ct);

    /// <summary>Lists direct children in a folder path.</summary>
    Task<IReadOnlyList<SharePointItem>> ListChildrenAsync(string driveId, string folderPath, CancellationToken ct);

    /// <summary>Uploads a file to a folder path.</summary>
    Task UploadFileAsync(string driveId, string folderPath, string localFilePath, string targetFileName, bool overwrite, CancellationToken ct);

    /// <summary>Downloads a file by its item id to a local path.</summary>
    Task DownloadFileAsync(string driveId, string itemId, string localFilePath, bool overwrite, CancellationToken ct);
}
