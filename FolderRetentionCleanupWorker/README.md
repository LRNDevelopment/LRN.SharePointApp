# FolderRetentionCleanupWorker

.NET 8 Worker Service that deletes files older than a configured retention period from configured root folders and their child folders.

## Configuration example

Edit `appsettings.json`:

```json
{
  "FolderCleanupSettings": {
    "Enabled": true,
    "RetentionWeeks": 12,
    "ScanIntervalMinutes": 60,
    "Folders": [
      "C:\\Temp\\CleanupRoot1",
      "D:\\Data\\CleanupRoot2"
    ],
    "DeleteEmptyFolders": true,
    "LogDeletedItems": true
  }
}
```

Rules:

- Files older than `RetentionWeeks` are deleted using `LastWriteTimeUtc`.
- If a folder contains only one file and that file is older than the retention period, the file is skipped and logged.
- If a folder contains multiple files, only files older than the retention period are deleted.
- If `DeleteEmptyFolders` is `true`, empty child folders are deleted after file cleanup.
- Configured root folders are never deleted.

Logs are written to the console and to `Logs/folder-cleanup-worker-.txt`.

## Run locally

```powershell
cd FolderRetentionCleanupWorker
dotnet run
```

## Publish

```powershell
dotnet publish .\FolderRetentionCleanupWorker.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

For a self-contained deployment:

```powershell
dotnet publish .\FolderRetentionCleanupWorker.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

## Install as Windows Service

Run PowerShell as Administrator:

```powershell
$serviceName = "FolderRetentionCleanupWorker"
$publishPath = "C:\Path\To\FolderRetentionCleanupWorker\publish"
$exePath = Join-Path $publishPath "FolderRetentionCleanupWorker.exe"

New-Service -Name $serviceName -BinaryPathName $exePath -DisplayName "Folder Retention Cleanup Worker" -StartupType Automatic
```

## Start, stop, and delete the Windows Service

Run PowerShell as Administrator:

```powershell
Start-Service -Name "FolderRetentionCleanupWorker"
Stop-Service -Name "FolderRetentionCleanupWorker"

sc.exe delete "FolderRetentionCleanupWorker"
```
