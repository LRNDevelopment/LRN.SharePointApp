using LRN.SharePointClient.Abstractions;
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

			var syncFolder = spFolder;
			if (item.SyncLatestWeekRawOnly && IsRawFileSync(item))
			{
				var latestRawFolder = await TryResolveLatestRawFolderAsync(driveId!, spFolder, item, ct);
				if (string.IsNullOrWhiteSpace(latestRawFolder))
				{
					_log.LogWarning("[{Type}] Latest raw folder could not be resolved from '{SP}'; skipped.", item.SyncronizeFileType, spFolder);
					continue;
				}

				syncFolder = latestRawFolder;
				_log.LogInformation("[{Type}] Latest raw folder resolved: {SP}", item.SyncronizeFileType, syncFolder);
			}

			Directory.CreateDirectory(item.ServerOutputFolder);
			_log.LogInformation("Downloading missing files: {SP} -> {Local}", syncFolder, item.ServerOutputFolder);

			var downloaded = await _sync.DownloadMissingAsync(
				driveId!,
				syncFolder,
				item.ServerOutputFolder,
				overwriteExisting: item.OverwriteExisting,
				onFileDownloaded: (remotePath, localPath) => NotifyFileSynchronizedAsync(item.SyncronizeFileType, remotePath, localPath, ct),
				onFileDownloadFailed: (remotePath, localPath, ex) => NotifyFileSyncFailedAsync(item.SyncronizeFileType, remotePath, localPath, ex, ct),
				ct: ct);

			_log.LogInformation("[{Type}] Downloaded {Count} file(s).", item.SyncronizeFileType, downloaded);
		}
	}

	private async Task<string?> TryResolveLatestRawFolderAsync(string driveId, string configuredSpFolder, LRN.SharePointClient.Models.UploadPathItem item, CancellationToken ct)
	{
		var rawRoot = TryGetRawReportsRoot(configuredSpFolder, item.RawFolderName);
		if (string.IsNullOrWhiteSpace(rawRoot))
			return null;

		var latestYear = await GetLatestChildFolderAsync(driveId, rawRoot, TryParseYearFolder, ct);
		if (latestYear == null)
			return null;

		var yearPath = CombineSpPath(rawRoot, latestYear.Name);
		var latestMonth = await GetLatestChildFolderAsync(driveId, yearPath, TryParseMonthFolder, ct);
		if (latestMonth == null)
			return yearPath;

		var monthPath = CombineSpPath(yearPath, latestMonth.Name);
		var latestWeek = await GetLatestChildFolderAsync(driveId, monthPath, TryParseWeekFolder, ct);
		if (latestWeek == null)
			return monthPath;

		var weekPath = CombineSpPath(monthPath, latestWeek.Name);
		if (string.IsNullOrWhiteSpace(item.RawFolderName))
			return weekPath;

		var rawFolder = (await _sp.ListChildrenAsync(driveId, weekPath, ct))
			.Where(child => child.IsFolder)
			.FirstOrDefault(child => string.Equals(child.Name, item.RawFolderName, StringComparison.OrdinalIgnoreCase));

		return rawFolder == null ? weekPath : CombineSpPath(weekPath, rawFolder.Name);
	}

	private async Task<LRN.SharePointClient.Models.SharePointItem?> GetLatestChildFolderAsync(
		string driveId,
		string parentFolder,
		Func<string, DateTimeOffset?> parseDate,
		CancellationToken ct)
	{
		var folders = (await _sp.ListChildrenAsync(driveId, parentFolder, ct))
			.Where(child => child.IsFolder)
			.Select(child => new
			{
				Item = child,
				SortDate = parseDate(child.Name) ?? child.LastModifiedUtc ?? DateTimeOffset.MinValue
			})
			.Where(child => child.SortDate > DateTimeOffset.MinValue)
			.OrderByDescending(child => child.SortDate)
			.ThenByDescending(child => child.Item.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();

		return folders.FirstOrDefault()?.Item;
	}

	private static bool IsRawFileSync(LRN.SharePointClient.Models.UploadPathItem item)
	{
		return string.Equals(item.SyncronizeFileType, "RawFile", StringComparison.OrdinalIgnoreCase);
	}

	private static string? TryGetRawReportsRoot(string configuredSpFolder, string rawFolderName)
	{
		var clean = NormalizeSpPath(configuredSpFolder);
		var segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		if (segments.Count == 0)
			return null;

		if (!string.IsNullOrWhiteSpace(rawFolderName) &&
			string.Equals(segments[^1], rawFolderName, StringComparison.OrdinalIgnoreCase))
		{
			segments.RemoveAt(segments.Count - 1);
		}

		var rawReportsIndex = segments.FindLastIndex(segment =>
			segment.Contains("Raw Reports", StringComparison.OrdinalIgnoreCase));

		if (rawReportsIndex >= 0)
			return string.Join("/", segments.Take(rawReportsIndex + 1));

		if (segments.Count >= 3)
			return string.Join("/", segments.Take(segments.Count - 3));

		return null;
	}

	private static DateTimeOffset? TryParseYearFolder(string name)
	{
		var match = Regex.Match(name, @"\b(20\d{2}|19\d{2})\b");
		if (!match.Success || !int.TryParse(match.Groups[1].Value, out var year))
			return null;

		return new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
	}

	private static DateTimeOffset? TryParseMonthFolder(string name)
	{
		var match = Regex.Match(name, @"^\s*(\d{1,2})\b");
		if (!match.Success || !int.TryParse(match.Groups[1].Value, out var month) || month < 1 || month > 12)
			return null;

		return new DateTimeOffset(2000, month, 1, 0, 0, 0, TimeSpan.Zero);
	}

	private static DateTimeOffset? TryParseWeekFolder(string name)
	{
		var matches = Regex.Matches(name, @"\b(\d{1,2})\.(\d{1,2})\.(\d{4})\b");
		if (matches.Count == 0)
			return null;

		var latest = matches
			.Select(match => DateTimeOffset.TryParseExact(
				match.Value,
				new[] { "M.d.yyyy", "MM.dd.yyyy", "M.dd.yyyy", "MM.d.yyyy" },
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal,
				out var date)
				? date
				: (DateTimeOffset?)null)
			.Where(date => date.HasValue)
			.Select(date => date!.Value)
			.OrderByDescending(date => date)
			.FirstOrDefault();

		return latest == default ? null : latest;
	}

	private static string NormalizeSpPath(string path)
	{
		var p = (path ?? "").Replace("\\", "/").Trim().Trim('/');
		while (p.Contains("//", StringComparison.Ordinal))
			p = p.Replace("//", "/", StringComparison.Ordinal);
		return p;
	}

	private static string CombineSpPath(params string[] parts)
	{
		var clean = parts
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.Select(p => p.Trim().Trim('/').Trim('\\'))
			.Where(p => p.Length > 0);
		return string.Join("/", clean);
	}


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
