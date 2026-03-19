# LRN.SharePointSynchronizer

Windows Worker Service to **synchronize SharePoint folders down to a server folder**.

It reads the same `UploadPaths` array used by `LRN.SharePointUploader`, but only processes items where:

```json
"IsSharePointUpload": false
```

## What it does

For each configured item:

1. Resolve the SharePoint drive id using Microsoft Graph (app-only).
2. Recursively list all folders/files under the configured SharePoint folder.
3. Download **missing** files/folders into the configured `ServerOutputFolder`.

By default it **does not overwrite** existing local files (`OverwriteExisting=false`).

## Config

Edit `appsettings.json`:

- `MasterFileProcessor:SharePoint` (or `BillingFrequency:SharePoint`) for Graph credentials.
- `SharePointSynchronizer:PollSeconds` for how often to re-run.
- `UploadPaths[]` with `IsSharePointUpload=false` items.

