using FolderRetentionCleanupWorker.Models;
using Microsoft.Extensions.Options;

namespace FolderRetentionCleanupWorker.Services;

public sealed class FolderCleanupService : IFolderCleanupService
{
    private readonly ILogger<FolderCleanupService> _logger;
    private readonly IOptionsMonitor<FolderCleanupSettings> _settings;

    public FolderCleanupService(
        ILogger<FolderCleanupService> logger,
        IOptionsMonitor<FolderCleanupSettings> settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public Task CleanupAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;

        if (!TryValidateSettings(settings))
        {
            return Task.CompletedTask;
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(settings.RetentionWeeks * -7);
        var summary = new CleanupSummary();

        _logger.LogInformation(
            "Starting cleanup cycle. RetentionWeeks={RetentionWeeks}, CutoffUtc={CutoffUtc:u}, RootFolderCount={RootFolderCount}",
            settings.RetentionWeeks,
            cutoffUtc,
            settings.Folders.Length);

        foreach (var folder in settings.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanRootFolder(folder, cutoffUtc, settings, summary, cancellationToken);
        }

        _logger.LogInformation(
            "Cleanup summary: ScannedRoots={ScannedRoots}, ScannedFolders={ScannedFolders}, ScannedFiles={ScannedFiles}, DeletedFiles={DeletedFiles}, SkippedFiles={SkippedFiles}, DeletedEmptyFolders={DeletedEmptyFolders}, Errors={Errors}",
            summary.ScannedRoots,
            summary.ScannedFolders,
            summary.ScannedFiles,
            summary.DeletedFiles,
            summary.SkippedFiles,
            summary.DeletedEmptyFolders,
            summary.Errors);

        return Task.CompletedTask;
    }

    private bool TryValidateSettings(FolderCleanupSettings settings)
    {
        if (settings.RetentionWeeks <= 0)
        {
            _logger.LogError("Invalid configuration: RetentionWeeks must be greater than zero");
            return false;
        }

        if (settings.Folders.Length == 0)
        {
            _logger.LogWarning("No folders are configured for cleanup");
            return false;
        }

        return true;
    }

    private void ScanRootFolder(
        string rootFolder,
        DateTime cutoffUtc,
        FolderCleanupSettings settings,
        CleanupSummary summary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            _logger.LogWarning("Skipping blank configured folder path");
            return;
        }

        var fullRootPath = Path.GetFullPath(rootFolder);

        if (!Directory.Exists(fullRootPath))
        {
            _logger.LogError("Configured folder does not exist: {RootFolder}", fullRootPath);
            summary.Errors++;
            return;
        }

        summary.ScannedRoots++;
        _logger.LogInformation("Scanning configured folder: {RootFolder}", fullRootPath);

        ScanDirectory(fullRootPath, cutoffUtc, settings, summary, cancellationToken);

        if (settings.DeleteEmptyFolders)
        {
            DeleteEmptyChildFolders(fullRootPath, summary, cancellationToken);
        }
    }

    private void ScanDirectory(
        string directory,
        DateTime cutoffUtc,
        FolderCleanupSettings settings,
        CleanupSummary summary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        summary.ScannedFolders++;

        IReadOnlyList<string> files = GetFiles(directory, summary);
        summary.ScannedFiles += files.Count;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessFile(file, files.Count, cutoffUtc, settings, summary);
        }

        foreach (var childDirectory in GetChildDirectories(directory, summary))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanDirectory(childDirectory, cutoffUtc, settings, summary, cancellationToken);
        }
    }

    private void ProcessFile(
        string file,
        int fileCountInFolder,
        DateTime cutoffUtc,
        FolderCleanupSettings settings,
        CleanupSummary summary)
    {
        try
        {
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(file);

            if (lastWriteTimeUtc >= cutoffUtc)
            {
                return;
            }

            if (fileCountInFolder == 1)
            {
                summary.SkippedFiles++;
                _logger.LogInformation(
                    "Skipped old file because it is the only file in its folder. File={File}, LastWriteTimeUtc={LastWriteTimeUtc:u}",
                    file,
                    lastWriteTimeUtc);
                return;
            }

            File.Delete(file);
            summary.DeletedFiles++;

            if (settings.LogDeletedItems)
            {
                _logger.LogInformation(
                    "Deleted old file. File={File}, LastWriteTimeUtc={LastWriteTimeUtc:u}",
                    file,
                    lastWriteTimeUtc);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.Errors++;
            _logger.LogError(ex, "Error processing file: {File}", file);
        }
    }

    private IReadOnlyList<string> GetFiles(string directory, CleanupSummary summary)
    {
        try
        {
            return Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.Errors++;
            _logger.LogError(ex, "Error reading files from folder: {Folder}", directory);
            return [];
        }
    }

    private IReadOnlyList<string> GetChildDirectories(string directory, CleanupSummary summary)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.Errors++;
            _logger.LogError(ex, "Error reading child folders from folder: {Folder}", directory);
            return [];
        }
    }

    private void DeleteEmptyChildFolders(
        string rootFolder,
        CleanupSummary summary,
        CancellationToken cancellationToken)
    {
        foreach (var childDirectory in GetChildDirectories(rootFolder, summary))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEmptyFolderRecursive(childDirectory, rootFolder, summary, cancellationToken);
        }
    }

    private void DeleteEmptyFolderRecursive(
        string directory,
        string rootFolder,
        CleanupSummary summary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var childDirectory in GetChildDirectories(directory, summary))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteEmptyFolderRecursive(childDirectory, rootFolder, summary, cancellationToken);
        }

        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            var fullRoot = Path.GetFullPath(rootFolder);

            if (string.Equals(fullDirectory, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Directory.EnumerateFileSystemEntries(fullDirectory).Any())
            {
                Directory.Delete(fullDirectory);
                summary.DeletedEmptyFolders++;
                _logger.LogInformation("Deleted empty folder: {Folder}", fullDirectory);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            summary.Errors++;
            _logger.LogError(ex, "Error deleting empty folder: {Folder}", directory);
        }
    }

    private sealed class CleanupSummary
    {
        public int ScannedRoots { get; set; }

        public int ScannedFolders { get; set; }

        public int ScannedFiles { get; set; }

        public int DeletedFiles { get; set; }

        public int SkippedFiles { get; set; }

        public int DeletedEmptyFolders { get; set; }

        public int Errors { get; set; }
    }
}
