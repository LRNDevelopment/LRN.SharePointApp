using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Models;
using LRN.SharePointClient.Options;
using LRN.SharePointClient.Sync;
using LRN.SharePointClient.Utils;
using LRN.SharePointUploader.ProcessLogging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

public sealed class SharePointUploaderWorker : BackgroundService
{
	private readonly ILogger<SharePointUploaderWorker> _log;
	private readonly ISharePointClient _sp;
	private readonly FolderSyncEngine _sync;
	private readonly UploaderWorkerOptions _opt;
	private readonly IServiceScopeFactory _scopeFactory;
	private readonly LrnStepLogOptions _stepOpt;
	private readonly ITeamsNotifier _teamsNotifier;
	private readonly SharePointGraphOptions _spOpt;

	public SharePointUploaderWorker(
		ILogger<SharePointUploaderWorker> log,
		ISharePointClient sp,
		FolderSyncEngine sync,
		IOptions<UploaderWorkerOptions> opt,
		IServiceScopeFactory scopeFactory,
		IOptions<LrnStepLogOptions> stepOpt,
		ITeamsNotifier teamsNotifier,
		IOptions<SharePointGraphOptions> spOpt)
	{
		_log = log;
		_sp = sp;
		_sync = sync;
		_opt = opt.Value;
		_scopeFactory = scopeFactory;
		_stepOpt = stepOpt.Value;
		_teamsNotifier = teamsNotifier;
		_spOpt = spOpt.Value;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!_opt.Enabled)
		{
			_log.LogInformation("SharePointUploaderWorker disabled.");
			return;
		}

		var once = _opt.PollSeconds <= 0;
		do
		{
			try
			{
				await RunOnceAsync(stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
			catch (Exception ex)
			{
				_log.LogError(ex, "SharePointUploaderWorker cycle failed.");
				await NotifyCycleErrorAsync(ex, stoppingToken);
			}

			if (once) break;

			try
			{
				await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _opt.PollSeconds)), stoppingToken);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

		} while (!stoppingToken.IsCancellationRequested);
	}

	private async Task RunOnceAsync(CancellationToken ct)
	{
		var paths = _opt.UploadPaths ?? new();
		if (paths.Count == 0)
		{
			_log.LogWarning("No UploadPaths configured.");
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

			if (item == null) continue;

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

			if (item.IsSharePointUpload)
			{
				Directory.CreateDirectory(item.ServerOutputFolder);
				_log.LogInformation("Uploading missing files: {Local} -> {SP}", item.ServerOutputFolder, spFolder);

				var uploaded = await _sync.UploadMissingAsync(
					driveId!,
					item.ServerOutputFolder,
					spFolder,
					includeSubfolders: item.IncludeSubfolders,
					overwriteExisting: item.OverwriteExisting,
					onFileUploaded: async (localFile, remotePath) =>
					{
						if (_stepOpt.Enabled)
						{
							var fn = Path.GetFileNameWithoutExtension(localFile);
							if (TryParseRunAndLab(fn, out var runIdToken, out var labName))
							{
								using var scope = _scopeFactory.CreateScope();
								var logger = scope.ServiceProvider.GetRequiredService<LrnProcessLogger>();

								await logger.TryLogUploadedFileAsync(
									runIdToken!,
									labName!,
									item.SyncronizeFileType ?? "",
									localFile,
									remotePath,
									ct);
							}
						}

						await NotifyFileUploadedAsync(item.SyncronizeFileType, localFile, remotePath, ct);
					},
					onFileUploadFailed: (localFile, remotePath, ex) => NotifyFileUploadFailedAsync(item.SyncronizeFileType, localFile, remotePath, ex, ct),
					ct: ct);

				_log.LogInformation("[{Type}] Uploaded {Count} file(s).", item.SyncronizeFileType, uploaded);
			}
			else
			{
				// Download mode (SharePoint -> Server)
				Directory.CreateDirectory(item.ServerOutputFolder);
				_log.LogInformation("Downloading missing files: {SP} -> {Local}", spFolder, item.ServerOutputFolder);

				var downloaded = await _sync.DownloadMissingAsync(
					driveId!,
					spFolder,
					item.ServerOutputFolder,
					overwriteExisting: item.OverwriteExisting,
					onFileDownloaded: null,
					onFileDownloadFailed: null,
					ct: ct);

				_log.LogInformation("[{Type}] Downloaded {Count} file(s).", item.SyncronizeFileType, downloaded);
			}
		}
	}

	private static bool TryParseRunAndLab(string fileNameNoExt, out string? runIdToken, out string? labName)
	{
		runIdToken = null;
		labName = null;

		if (string.IsNullOrWhiteSpace(fileNameNoExt))
			return false;

		var parts = fileNameNoExt.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length < 2)
			return false;

		runIdToken = parts[0];
		labName = parts[1];
		return !string.IsNullOrWhiteSpace(runIdToken) && !string.IsNullOrWhiteSpace(labName);
	}


	private Task NotifyFileUploadedAsync(string fileType, string localFile, string remotePath, CancellationToken ct)
	{
		var fileName = Path.GetFileName(localFile);
		var remoteUrl = SharePointWebLinkBuilder.TryBuildFileUrl(_spOpt, remotePath);

		var message = new StringBuilder()
			.AppendLine("🟢 File uploaded successfully.\n")
			.AppendLine($"📁 Type: {fileType}\n")
			.AppendLine(string.IsNullOrWhiteSpace(remoteUrl)
				? $"📁 SharePoint: {remotePath}\n"
				: $"📄 SharePoint: [{fileName}]({remoteUrl})\n")
			.Append($"Source: {localFile}")
			.ToString();

		return _teamsNotifier.SendAsync("🤖 LRN : SharePoint Uploader", message, ct);
	}

	private Task NotifyFileUploadFailedAsync(string fileType, string localFile, string remotePath, Exception ex, CancellationToken ct)
	{
		var fileName = Path.GetFileName(localFile);
		var remoteUrl = SharePointWebLinkBuilder.TryBuildFileUrl(_spOpt, remotePath);

		var message = new StringBuilder()
			.AppendLine("⚠️ File upload failed.\n")
			.AppendLine($"Type: {fileType} \n")
			.AppendLine(string.IsNullOrWhiteSpace(remoteUrl)
				? $"📁 SharePoint: {remotePath} \n	"
				: $"📄 SharePoint: [{fileName}]({remoteUrl}\n")
			.AppendLine($"Source: {localFile} \n")
			.Append($"Error: {ex.Message}")
			.ToString();

		return _teamsNotifier.SendAsync("🤖 LRN : SharePoint Uploader", message, ct);
	}

	private Task NotifyCycleErrorAsync(Exception ex, CancellationToken ct)
	{
		var message = new StringBuilder()
			.AppendLine("❌ SharePoint uploader cycle failed.")
			.Append($"Error: {ex.Message}")
			.ToString();

		return _teamsNotifier.SendAsync("LRN : SharePoint Uploader", message, ct);
	}

}
