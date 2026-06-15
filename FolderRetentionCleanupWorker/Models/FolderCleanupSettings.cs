namespace FolderRetentionCleanupWorker.Models;

public sealed class FolderCleanupSettings
{
    public const string SectionName = "FolderCleanupSettings";

    public bool Enabled { get; set; }

    public int RetentionWeeks { get; set; }

    public int ScanIntervalMinutes { get; set; }

    public string[] Folders { get; set; } = [];

    public bool DeleteEmptyFolders { get; set; }

    public bool LogDeletedItems { get; set; } = true;
}
