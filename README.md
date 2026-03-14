# LRN SharePoint Folder Sync (Uploader + Synchronizer)

This solution contains **two Windows Worker Services** that keep a SharePoint folder and a server folder in sync.

## Projects

- **LRN.SharePointClient**
  - Minimal Microsoft Graph (app-only) SharePoint client.
  - Supports: resolve drive id, ensure folders, list, upload, download.

- **LRN.SharePointUploader**
  - Syncs **Server -> SharePoint** (items where `IsSharePointUpload=true`).
  - Can also run **SharePoint -> Server** for items where `IsSharePointUpload=false` (if you keep it in one service).
  - While uploading, it parses file name:
    - `RunId` = first segment before `_`
    - `LabName` = second segment
    - Example: `20260226R0037_PCRLabsofAmerica_CodingValidated` => RunId `20260226R0037`, LabName `PCRLabsofAmerica`
  - If `LrnStepLog.Enabled=true`, it inserts a row into `dbo.LRN_STEP_LOG` with `RunId`, `LabName`, and `SyncronizeFileType`.

- **LRN.SharePointSynchronizer**
  - Syncs **SharePoint -> Server** (items where `IsSharePointUpload=false`).

## Configuration

### SharePoint (Microsoft Graph app-only)

In either service `appsettings.json`, fill **one** of these sections (both are supported for backward compatibility):

- `BillingFrequency:SharePoint`
- `MasterFileProcessor:SharePoint`
- `SharePoint`

Required keys:

```json
{
  "TenantId": "<tenant-guid>",
  "ClientId": "<app-client-id>",
  "ClientSecret": "<client-secret>",
  "SiteHostName": "contoso.sharepoint.com",
  "SitePath": "/sites/YourSite",
  "DriveName": "Documents"
}
```

> Your existing config uses `Hostname`; that also works.

### UploadPaths (rules)

Both services read a root array named **`UploadPaths`**:

```json
{
  "IsSharePointUpload": true,
  "SyncronizeFileType": "Coding Validation Report",
  "ServerOutputFolder": "C:\\LRN-Files\\Automation\\LRN-Master-Output\\CodinngValidationoutputs",
  "SharePointFolder": "https://...AllItems.aspx?id=...",
  "IncludeSubfolders": true,
  "OverwriteExisting": false
}
```

`SharePointFolder` can be:
- a **drive-relative path** like `10. Automation/LRN-Output/Averages Database`
- OR a **SharePoint folder link** (AllItems.aspx?id=...)

## Step log (LRN_STEP_LOG)

In **LRN.SharePointUploader/appsettings.json**:

```json
"LrnStepLog": {
  "Enabled": true,
  "ConnectionString": "Server=...;Database=...;Trusted_Connection=True;TrustServerCertificate=True"
}
```

Default insert SQL assumes these columns exist:

- `RunId`
- `LabName`
- `SyncronizeFileType`
- `StepName`
- `StepStatus`
- `FileName`
- `SharePointPath`
- `CreatedUtc`

If your table schema is different, override `LrnStepLog:InsertSql` with your exact insert or stored procedure call.

## Windows Service install

Publish each project (Release):

```powershell
dotnet publish .\LRN.SharePointUploader\LRN.SharePointUploader.csproj -c Release -r win-x64
dotnet publish .\LRN.SharePointSynchronizer\LRN.SharePointSynchronizer.csproj -c Release -r win-x64
```

Install service:

```powershell
sc.exe create "LRN SharePoint Uploader" binPath= "C:\\Deploy\\Uploader\\LRN.SharePointUploader.exe" start= auto
sc.exe start "LRN SharePoint Uploader"

sc.exe create "LRN SharePoint Synchronizer" binPath= "C:\\Deploy\\Synchronizer\\LRN.SharePointSynchronizer.exe" start= auto
sc.exe start "LRN SharePoint Synchronizer"
```
