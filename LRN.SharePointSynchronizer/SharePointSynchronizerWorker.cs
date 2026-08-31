using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Models;
using LRN.SharePointClient.Options;
using LRN.SharePointClient.Sync;
using LRN.SharePointClient.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public sealed class SharePointSynchronizerWorker : BackgroundService
{
    private readonly ILogger<SharePointSynchronizerWorker> _log;
    private readonly ISharePointClient _sp;
    private readonly FolderSyncEngine _sync;
    private readonly SynchronizerWorkerOptions _opt;
    private readonly ITeamsNotifier _teamsNotifier;
    private readonly SharePointGraphOptions _spOpt;

    public SharePointSynchronizerWorker(
        ILogger<SharePointSynchronizerWorker> log,
        ISharePointClient sp,
        FolderSyncEngine sync,
        IOptions<SynchronizerWorkerOptions> opt,
        ITeamsNotifier teamsNotifier,
        IOptions<SharePointGraphOptions> spOpt)
    {
        _log = log;
        _sp = sp;
        _sync = sync;
        _opt = opt.Value;
        _teamsNotifier = teamsNotifier;
        _spOpt = spOpt.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("SharePointSynchronizerWorker disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "SharePointSynchronizerWorker cycle failed.");
                await NotifyCycleErrorAsync(ex, stoppingToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _opt.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var paths = (_opt.UploadPaths ?? new()).Where(p => p != null && !p.IsSharePointUpload).ToList();
        if (paths.Count == 0)
        {
            _log.LogDebug("No download paths configured (UploadPaths with IsSharePointUpload=false).");
            return;
        }

        var driveId = await _sp.TryResolveDriveIdAsync(ct);
        if (string.IsNullOrWhiteSpace(driveId))
        {
            _log.LogError("Unable to resolve SharePoint drive id. Check SharePoint settings.");
            return;
        }

        foreach (var item in paths)
        {
            ct.ThrowIfCancellationRequested();

            var spFolder = SharePointFolderLinkParser.ToDriveRelativeFolderPath(item.SharePointFolder);
            if (string.IsNullOrWhiteSpace(spFolder))
            {
                _log.LogWarning("[{Type}] SharePointFolder is empty/invalid; skipped.", item.SyncronizeFileType);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ServerOutputFolder))
            {
                _log.LogWarning("[{Type}] ServerOutputFolder is empty; skipped.", item.SyncronizeFileType);
                continue;
            }

            var localRoot = GetLocalDownloadRoot(item, spFolder);
            Directory.CreateDirectory(localRoot);

            if (item.SyncLatestWeekOnly)
            {
                var downloaded = await SyncLatestWeekFolderAsync(driveId!, spFolder, item, ct);
                _log.LogInformation("[{Type}] Latest available week sync downloaded {Count} file(s).", item.SyncronizeFileType, downloaded);
                continue;
            }

            if (ShouldSyncLatestWeekRawOnly(item))
            {
                var downloaded = await SyncLatestWeekRawFilesAsync(driveId!, spFolder, item, ct);
                _log.LogInformation("[{Type}] Latest week raw sync downloaded {Count} file(s).", item.SyncronizeFileType, downloaded);
                continue;
            }

            _log.LogInformation("Downloading missing files: {SP} -> {Local}", spFolder, localRoot);

            var normalDownloaded = await _sync.DownloadMissingAsync(
                driveId!,
                spFolder,
                localRoot,
                overwriteExisting: item.OverwriteExisting,
                onFileDownloaded: (remotePath, localPath) => NotifyFileSynchronizedAsync(item.SyncronizeFileType, remotePath, localPath, ct),
                onFileDownloadFailed: (remotePath, localPath, ex) => NotifyFileSyncFailedAsync(item.SyncronizeFileType, remotePath, localPath, ex, ct),
                ct: ct);

            _log.LogInformation("[{Type}] Downloaded {Count} file(s).", item.SyncronizeFileType, normalDownloaded);
        }
    }

    private async Task<int> SyncLatestWeekFolderAsync(string driveId, string configuredSpFolder, UploadPathItem item, CancellationToken ct)
    {
        var latest = await FindLatestAvailableWeekFolderAsync(driveId, configuredSpFolder, ct);
        if (latest == null)
        {
            _log.LogWarning("[{Type}] No week folder found under {Root}.", item.SyncronizeFileType, configuredSpFolder);
            return 0;
        }

        var localRoot = GetLocalDownloadRoot(item, latest.WeekSpFolder);
        Directory.CreateDirectory(localRoot);

        _log.LogInformation(
            "[{Type}] Syncing latest available week folder: {SP} -> {Local}",
            item.SyncronizeFileType,
            latest.WeekSpFolder,
            localRoot);

        return await _sync.DownloadMissingAsync(
            driveId,
            latest.WeekSpFolder,
            localRoot,
            overwriteExisting: item.OverwriteExisting,
            onFileDownloaded: (remotePath, localPath) => NotifyFileSynchronizedAsync(item.SyncronizeFileType, remotePath, localPath, ct),
            onFileDownloadFailed: (remotePath, localPath, ex) => NotifyFileSyncFailedAsync(item.SyncronizeFileType, remotePath, localPath, ex, ct),
            ct: ct);
    }

    private async Task<int> SyncLatestWeekRawFilesAsync(string driveId, string configuredSpFolder, UploadPathItem item, CancellationToken ct)
    {
        var probe = ResolveLatestRawProbe(configuredSpFolder, item.RawFolderName);
        var rootFolder = probe.RootFolder;
        var rawFolderName = !string.IsNullOrWhiteSpace(item.RawFolderName) ? item.RawFolderName!.Trim() : probe.RawFolderName;

        _log.LogInformation("[{Type}] Finding latest raw folder. Root='{Root}', RawFolderName='{RawFolderName}'", item.SyncronizeFileType, rootFolder, rawFolderName);

        var yearFolder = await GetLatestFolderAsync(driveId, rootFolder, TryGetYear, ct);
        if (yearFolder == null)
        {
            _log.LogWarning("[{Type}] No year folder found under {Root}.", item.SyncronizeFileType, rootFolder);
            return 0;
        }

        var yearSpFolder = CombineSpPath(rootFolder, yearFolder.Name);
        var monthFolder = await GetLatestFolderAsync(driveId, yearSpFolder, TryGetMonthNumber, ct);
        if (monthFolder == null)
        {
            _log.LogWarning("[{Type}] No month folder found under {YearFolder}.", item.SyncronizeFileType, yearSpFolder);
            return 0;
        }

        var monthSpFolder = CombineSpPath(yearSpFolder, monthFolder.Name);
        var weekFolder = await GetLatestWeekFolderAsync(driveId, monthSpFolder, ct);
        if (weekFolder == null)
        {
            _log.LogWarning("[{Type}] No week folder found under {MonthFolder}.", item.SyncronizeFileType, monthSpFolder);
            return 0;
        }

        var weekSpFolder = CombineSpPath(monthSpFolder, weekFolder.Name);
        var rawFolders = await ResolveRawFoldersAsync(driveId, weekSpFolder, rawFolderName, ct);
        if (rawFolders.Count == 0)
        {
            _log.LogWarning("[{Type}] No raw folder found under {WeekFolder}.", item.SyncronizeFileType, weekSpFolder);
            return 0;
        }

        var localYearFolder = ResolveExistingLocalFolder(item.ServerOutputFolder, yearFolder.Name, YearFolderMatches);
        var localMonthFolder = ResolveExistingLocalFolder(localYearFolder, monthFolder.Name, MonthFolderMatches);
        var localWeekFolder = ResolveExistingLocalFolder(localMonthFolder, weekFolder.Name, WeekFolderMatches);

        var downloaded = 0;
        foreach (var rawFolder in rawFolders)
        {
            ct.ThrowIfCancellationRequested();

            var rawSpFolder = CombineSpPath(weekSpFolder, rawFolder.Name);
            var localRawFolder = ResolveExistingLocalFolder(localWeekFolder, rawFolder.Name, NormalizedNameEquals);

            _log.LogInformation("[{Type}] Syncing raw folder: {SP} -> {Local}", item.SyncronizeFileType, rawSpFolder, localRawFolder);

            downloaded += await _sync.DownloadMissingAsync(
                driveId,
                rawSpFolder,
                localRawFolder,
                overwriteExisting: item.OverwriteExisting,
                onFileDownloaded: (remotePath, localPath) => NotifyFileSynchronizedAsync(item.SyncronizeFileType, remotePath, localPath, ct),
                onFileDownloadFailed: (remotePath, localPath, ex) => NotifyFileSyncFailedAsync(item.SyncronizeFileType, remotePath, localPath, ex, ct),
                fileNameFilter: name => FileNameMatches(name, item.FileNamePattern),
                ct: ct);
        }

        return downloaded;
    }

    private async Task<LatestWeekFolder?> FindLatestAvailableWeekFolderAsync(string driveId, string rootFolder, CancellationToken ct)
    {
        var yearFolders = await GetFolderCandidatesAsync(driveId, rootFolder, TryGetYear, ct);
        foreach (var yearFolder in yearFolders)
        {
            var yearSpFolder = CombineSpPath(rootFolder, yearFolder.Item.Name);
            var monthFolders = await GetFolderCandidatesAsync(driveId, yearSpFolder, TryGetMonthNumber, ct);

            foreach (var monthFolder in monthFolders)
            {
                var monthSpFolder = CombineSpPath(yearSpFolder, monthFolder.Item.Name);
                var weekFolder = await GetLatestWeekFolderAsync(driveId, monthSpFolder, ct);
                if (weekFolder == null)
                    continue;

                return new LatestWeekFolder(CombineSpPath(monthSpFolder, weekFolder.Name));
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<FolderCandidate>> GetFolderCandidatesAsync(string driveId, string folderPath, Func<string, int?> getSortKey, CancellationToken ct)
    {
        return (await _sp.ListChildrenAsync(driveId, folderPath, ct))
            .Where(x => x.IsFolder)
            .Select(x => new FolderCandidate(x, getSortKey(x.Name)))
            .Where(x => x.SortKey.HasValue)
            .OrderByDescending(x => x.SortKey!.Value)
            .ThenByDescending(x => x.Item.LastModifiedUtc ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static bool ShouldSyncLatestWeekRawOnly(UploadPathItem item)
    {
        return item.SyncLatestWeekRawOnly;
    }

    private static string GetLocalDownloadRoot(UploadPathItem item, string spFolder)
    {
        if (!item.PreserveSharePointFolderStructure)
            return item.ServerOutputFolder;

        var datedPath = GetDatedRelativeFolderPath(spFolder);
        if (string.IsNullOrWhiteSpace(datedPath))
            return item.ServerOutputFolder;

        var parts = datedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = item.ServerOutputFolder;
        for (var i = 0; i < parts.Length; i++)
        {
            Func<string, string, bool> matches = i switch
            {
                0 => YearFolderMatches,
                1 => MonthFolderMatches,
                2 => WeekFolderMatches,
                _ => NormalizedNameEquals
            };
            current = ResolveExistingLocalFolder(current, parts[i], matches);
        }

        return current;
    }

    /// <summary>
    /// Reuses an existing local subfolder that matches the SharePoint folder name
    /// (ignoring whitespace/format differences like "07.July" vs "07. July");
    /// only falls back to the SharePoint name when no local match exists.
    /// When several local folders match, the oldest one wins.
    /// </summary>
    private static string ResolveExistingLocalFolder(string parentFolder, string spName, Func<string, string, bool> matches)
    {
        if (!Directory.Exists(parentFolder))
            return Path.Combine(parentFolder, spName);

        var match = Directory.EnumerateDirectories(parentFolder)
            .Select(p => new DirectoryInfo(p))
            .Where(d => matches(d.Name, spName))
            .OrderBy(d => d.CreationTimeUtc)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return match?.FullName ?? Path.Combine(parentFolder, spName);
    }

    private static bool YearFolderMatches(string localName, string spName)
        => NormalizedNameEquals(localName, spName)
           || (TryGetYear(localName) is int local && TryGetYear(spName) is int sp && local == sp);

    private static bool MonthFolderMatches(string localName, string spName)
        => NormalizedNameEquals(localName, spName)
           || (TryGetMonthNumber(localName) is int local && TryGetMonthNumber(spName) is int sp && local == sp);

    private static bool WeekFolderMatches(string localName, string spName)
        => NormalizedNameEquals(localName, spName)
           || (TryGetWeekEndDate(localName) is DateTime local && TryGetWeekEndDate(spName) is DateTime sp && local == sp);

    private static bool NormalizedNameEquals(string localName, string spName)
        => string.Equals(
            Regex.Replace(localName, @"\s+", ""),
            Regex.Replace(spName, @"\s+", ""),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetDatedRelativeFolderPath(string spFolder)
    {
        var parts = spFolder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var yearIndex = Array.FindIndex(parts, p => TryGetYear(p).HasValue);
        if (yearIndex < 0)
            return null;

        return string.Join('/', parts.Skip(yearIndex));
    }

    private static (string RootFolder, string? RawFolderName) ResolveLatestRawProbe(string configuredSpFolder, string? rawFolderName)
    {
        var parts = configuredSpFolder.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var yearIndex = parts.FindIndex(p => TryGetYear(p).HasValue);
        if (yearIndex >= 0)
        {
            var root = string.Join('/', parts.Take(yearIndex));
            string? detectedRaw = null;
            if (string.IsNullOrWhiteSpace(rawFolderName))
                detectedRaw = parts.LastOrDefault(p => p.Contains("Raw", StringComparison.OrdinalIgnoreCase));

            return (root, detectedRaw);
        }

        return (configuredSpFolder.Trim('/'), string.IsNullOrWhiteSpace(rawFolderName) ? null : rawFolderName);
    }

    private async Task<SharePointItem?> GetLatestFolderAsync(string driveId, string folderPath, Func<string, int?> getSortKey, CancellationToken ct)
    {
        var folders = (await _sp.ListChildrenAsync(driveId, folderPath, ct))
            .Where(x => x.IsFolder)
            .Select(x => new { Item = x, Key = getSortKey(x.Name) })
            .Where(x => x.Key.HasValue)
            .OrderByDescending(x => x.Key!.Value)
            .ThenByDescending(x => x.Item.LastModifiedUtc ?? DateTimeOffset.MinValue)
            .ToList();

        return folders.FirstOrDefault()?.Item;
    }

    private async Task<SharePointItem?> GetLatestWeekFolderAsync(string driveId, string monthFolder, CancellationToken ct)
    {
        var folders = (await _sp.ListChildrenAsync(driveId, monthFolder, ct))
            .Where(x => x.IsFolder)
            .Select(x => new { Item = x, WeekEnd = TryGetWeekEndDate(x.Name) })
            .OrderByDescending(x => x.WeekEnd ?? DateTime.MinValue)
            .ThenByDescending(x => x.Item.LastModifiedUtc ?? DateTimeOffset.MinValue)
            .ToList();

        return folders.FirstOrDefault()?.Item;
    }

    private async Task<IReadOnlyList<SharePointItem>> ResolveRawFoldersAsync(string driveId, string weekSpFolder, string? rawFolderName, CancellationToken ct)
    {
        var children = await _sp.ListChildrenAsync(driveId, weekSpFolder, ct);
        var folders = children
            .Where(x => x.IsFolder)
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(rawFolderName))
        {
            var named = folders
                .Where(x => x.Name.Contains(rawFolderName, StringComparison.OrdinalIgnoreCase) || rawFolderName.Contains(x.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (named.Count > 0)
                return named;
        }

        return folders.Where(x => x.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static int? TryGetYear(string name)
    {
        var m = Regex.Match(name, @"(?<!\d)(20\d{2})(?!\d)");
        return m.Success && int.TryParse(m.Value, out var year) ? year : null;
    }

    private static int? TryGetMonthNumber(string name)
    {
        var numeric = Regex.Match(name, @"(?<!\d)(0?[1-9]|1[0-2])(?=\s*[\.\-_ ]|$)");
        if (numeric.Success && int.TryParse(numeric.Groups[1].Value, out var month))
            return month;

        for (var i = 1; i <= 12; i++)
        {
            var full = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(i);
            var shortName = CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(i);
            if (name.Contains(full, StringComparison.OrdinalIgnoreCase) || name.Contains(shortName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return null;
    }

    private static DateTime? TryGetWeekEndDate(string name)
    {
        var matches = Regex.Matches(name, @"(?<!\d)(\d{1,2})\.(\d{1,2})\.(\d{4})(?!\d)");
        if (matches.Count > 0)
        {
            var last = matches[^1];
            if (int.TryParse(last.Groups[1].Value, out var mm) &&
                int.TryParse(last.Groups[2].Value, out var dd) &&
                int.TryParse(last.Groups[3].Value, out var yyyy))
            {
                try { return new DateTime(yyyy, mm, dd); } catch { }
            }
        }

        return null;
    }

    private static bool FileNameMatches(string fileName, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;

        var regex = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CombineSpPath(params string?[] parts)
    {
        var clean = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim().Trim('/').Trim('\\'))
            .Where(p => p.Length > 0);
        return string.Join("/", clean);
    }

    private sealed record LatestWeekFolder(string WeekSpFolder);

    private sealed record FolderCandidate(SharePointItem Item, int? SortKey);

    private Task NotifyFileSynchronizedAsync(string fileType, string remotePath, string localPath, CancellationToken ct)
    {
        var fileName = Path.GetFileName(localPath);
        var remoteUrl = SharePointWebLinkBuilder.TryBuildFileUrl(_spOpt, remotePath);

        var message = new StringBuilder()
            .AppendLine("🟢 File synchronized successfully.\n")
            .AppendLine($"📁 Type: {fileType}  \n")
            .AppendLine(string.IsNullOrWhiteSpace(remoteUrl)
                ? $"📁 Source: {remotePath}\n"
                : $"📄 Source: [{fileName}]({remoteUrl})\n")
            .Append($"Destination: {localPath}")
            .ToString();

        return _teamsNotifier.SendAsync("🤖 LRN : SharePoint Synchronizer", message, ct);
    }

    private Task NotifyFileSyncFailedAsync(string fileType, string remotePath, string localPath, Exception ex, CancellationToken ct)
    {
        var fileName = Path.GetFileName(localPath);
        var remoteUrl = SharePointWebLinkBuilder.TryBuildFileUrl(_spOpt, remotePath);

        var message = new StringBuilder()
            .AppendLine("⚠️ File synchronization failed.\n")
            .AppendLine($"📁 Type: {fileType}\n")
            .AppendLine(string.IsNullOrWhiteSpace(remoteUrl)
                ? $"📁 Source: {remotePath}\n"
                : $"📄 Source: [{fileName}]({remoteUrl})\n")
            .AppendLine($"Destination: {localPath}\n")
            .Append($"❌ Error: {ex.Message}")
            .ToString();

        return _teamsNotifier.SendAsync("🤖 LRN : SharePoint Synchronizer", message, ct);
    }

    private Task NotifyCycleErrorAsync(Exception ex, CancellationToken ct)
    {
        var message = new StringBuilder()
            .AppendLine("❌ SharePoint synchronizer cycle failed.")
            .Append($"Error: {ex.Message}")
            .ToString();

        return _teamsNotifier.SendAsync("LRN : SharePoint Synchronizer", message, ct);
    }
}
