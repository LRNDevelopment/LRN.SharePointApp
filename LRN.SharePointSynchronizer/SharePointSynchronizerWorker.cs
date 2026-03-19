using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Options;
using LRN.SharePointClient.Sync;
using LRN.SharePointClient.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

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

			Directory.CreateDirectory(item.ServerOutputFolder);
			_log.LogInformation("Downloading missing files: {SP} -> {Local}", spFolder, item.ServerOutputFolder);

			var downloaded = await _sync.DownloadMissingAsync(
				driveId!,
				spFolder,
				item.ServerOutputFolder,
				overwriteExisting: item.OverwriteExisting,
				onFileDownloaded: (remotePath, localPath) => NotifyFileSynchronizedAsync(item.SyncronizeFileType, remotePath, localPath, ct),
				onFileDownloadFailed: (remotePath, localPath, ex) => NotifyFileSyncFailedAsync(item.SyncronizeFileType, remotePath, localPath, ex, ct),
				ct: ct);

			_log.LogInformation("[{Type}] Downloaded {Count} file(s).", item.SyncronizeFileType, downloaded);
		}
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
