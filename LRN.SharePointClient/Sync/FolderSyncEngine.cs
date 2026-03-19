using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Models;
using Microsoft.Extensions.Logging;

namespace LRN.SharePointClient.Sync;

public sealed class FolderSyncEngine
{
	private readonly ISharePointClient _sp;
	private readonly ILogger<FolderSyncEngine> _log;

	public FolderSyncEngine(ISharePointClient sp, ILogger<FolderSyncEngine> log)
	{
		_sp = sp;
		_log = log;
	}

	public async Task<int> UploadMissingAsync(
		string driveId,
		string localRoot,
		string sharePointRootFolder,
		bool includeSubfolders,
		bool overwriteExisting,
		Func<string, string, Task>? onFileUploaded,
		Func<string, string, Exception, Task>? onFileUploadFailed,
		CancellationToken ct)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(localRoot) || !Directory.Exists(localRoot))
				return 0;

			var search = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
			var files = Directory.EnumerateFiles(localRoot, "*.*", search)
				.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
				.ToList();

			int uploaded = 0;
			foreach (var file in files)
			{
				ct.ThrowIfCancellationRequested();

				var rel = Path.GetRelativePath(localRoot, file);
				var relFolder = (Path.GetDirectoryName(rel) ?? "")
					.Replace('\\', '/')
					.Replace('\u202A', ' ')
					.Replace('\u202B', ' ')
					.Replace('\u202C', ' ');
				relFolder = relFolder.Replace("\\", "/").Trim('/');

				var spFolder = CombineSpPath(sharePointRootFolder, relFolder);

				var fileName = Path.GetFileName(file);
				var remotePath = string.IsNullOrWhiteSpace(spFolder) ? fileName : spFolder + "/" + fileName;

				_log.LogInformation(
					"Upload loop item. LocalFile='{LocalFile}', FileName='{FileName}', SpFolder='{SpFolder}', RemotePath='{RemotePath}', Overwrite={Overwrite}",
					file, fileName, spFolder, remotePath, overwriteExisting);

				var exists = await _sp.TryGetItemByPathAsync(driveId, remotePath, ct);

				if (exists != null && !exists.IsFolder && (exists.Size ?? 0) == 0)
				{
					_log.LogWarning(
						"Existing file is zero bytes, forcing overwrite. RemotePath='{RemotePath}', ItemId='{ItemId}'",
						remotePath, exists.ItemId);

					await _sp.UploadFileAsync(driveId, spFolder, file, fileName, true, ct);
					uploaded++;
					continue;
				}

				if (exists != null && !overwriteExisting)
				{
					continue;
				}

				try
				{
					await _sp.UploadFileAsync(driveId, spFolder, file, fileName, overwriteExisting, ct);
					uploaded++;


					var verify = await _sp.TryGetItemByPathAsync(driveId, remotePath, ct);
					if (verify == null)
					{
						throw new InvalidOperationException($"Upload reported success, but file was not found at '{remotePath}'.");
					}

					_log.LogInformation("Uploaded: LocalFile='{LocalFile}' -> RemotePath='{RemotePath}'", file, remotePath);

					if (onFileUploaded != null)
						await onFileUploaded(file, remotePath);
				}
				catch (Exception ex)
				{
					_log.LogError(ex, "Upload failed: LocalFile='{LocalFile}' -> RemotePath='{RemotePath}'", file, remotePath);

					if (onFileUploadFailed != null)
						await onFileUploadFailed(file, remotePath, ex);
				}
			}

			return uploaded;
		}
		catch (Exception ex)
		{
			throw;
		}

	}

	public async Task<int> DownloadMissingAsync(
		string driveId,
		string sharePointRootFolder,
		string localRoot,
		bool overwriteExisting,
		Func<string, string, Task>? onFileDownloaded,
		Func<string, string, Exception, Task>? onFileDownloadFailed,
		CancellationToken ct)
	{
		Directory.CreateDirectory(localRoot);
		return await DownloadFolderRecursiveAsync(
			driveId,
			sharePointRootFolder,
			localRoot,
			overwriteExisting,
			onFileDownloaded,
			onFileDownloadFailed,
			ct);
	}

	private async Task<int> DownloadFolderRecursiveAsync(
		string driveId,
		string spFolder,
		string localFolder,
		bool overwrite,
		Func<string, string, Task>? onFileDownloaded,
		Func<string, string, Exception, Task>? onFileDownloadFailed,
		CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		Directory.CreateDirectory(localFolder);

		var children = await _sp.ListChildrenAsync(driveId, spFolder, ct);
		int downloaded = 0;

		foreach (var item in children.OrderBy(i => i.IsFolder ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
		{
			ct.ThrowIfCancellationRequested();

			if (item.IsFolder)
			{
				var nextSp = CombineSpPath(spFolder, item.Name);
				var nextLocal = Path.Combine(localFolder, item.Name);
				downloaded += await DownloadFolderRecursiveAsync(
					driveId,
					nextSp,
					nextLocal,
					overwrite,
					onFileDownloaded,
					onFileDownloadFailed,
					ct);
				continue;
			}

			var localPath = Path.Combine(localFolder, item.Name);
			if (File.Exists(localPath) && !overwrite)
				continue;

			try
			{
				await _sp.DownloadFileAsync(driveId, item.ItemId, localPath, overwrite, ct);
				downloaded++;

				var remotePath = string.IsNullOrWhiteSpace(spFolder) ? item.Name : spFolder + "/" + item.Name;
				_log.LogInformation("Downloaded: {Remote} -> {Local}", remotePath, localPath);

				if (onFileDownloaded != null)
					await onFileDownloaded(remotePath, localPath);
			}
			catch (Exception ex)
			{
				var remotePath = string.IsNullOrWhiteSpace(spFolder) ? item.Name : spFolder + "/" + item.Name;
				_log.LogError(ex, "Download failed: {Remote} -> {Local}", remotePath, localPath);

				if (onFileDownloadFailed != null)
					await onFileDownloadFailed(remotePath, localPath, ex);
			}
		}

		return downloaded;
	}

	private static string CombineSpPath(params string[] parts)
	{
		var clean = parts
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.Select(p => p.Trim().Trim('/').Trim('\\'))
			.Where(p => p.Length > 0);
		return string.Join("/", clean);
	}
}
