namespace FolderRetentionCleanupWorker.Services;

public interface IFolderCleanupService
{
    Task CleanupAsync(CancellationToken cancellationToken);
}
