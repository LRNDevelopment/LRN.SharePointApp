using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Models;
using LRN.SharePointClient.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace LRN.SharePointClient.Graph
{
	public class GraphSharePointClient : ISharePointClient
	{
		private readonly GraphServiceClient _graphClient;
		private readonly HttpClient _httpClient;
		private readonly ILogger<GraphSharePointClient> _logger;
		private readonly SharePointGraphOptions _options;

		private const int LargeFileThreshold = 4 * 1024 * 1024;
		private const int ChunkSize = 5 * 1024 * 1024;

		public GraphSharePointClient(
			GraphServiceClient graphClient,
			HttpClient httpClient,
			IOptions<SharePointGraphOptions> options,
			ILogger<GraphSharePointClient> logger)
		{
			_graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
			_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
			_options = options?.Value ?? throw new ArgumentNullException(nameof(options));
			_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		}

		public async Task<string?> TryResolveDriveIdAsync(CancellationToken ct)
		{
			var hostName = string.IsNullOrWhiteSpace(_options.SiteHostName)
				? _options.Hostname
				: _options.SiteHostName;

			if (string.IsNullOrWhiteSpace(hostName) || string.IsNullOrWhiteSpace(_options.SitePath))
				return null;

			var normalizedSitePath = _options.SitePath.StartsWith("/")
				? _options.SitePath
				: "/" + _options.SitePath;

			var siteId = $"{hostName}:{normalizedSitePath}";

			var site = await _graphClient
				.Sites[siteId]
				.GetAsync(cancellationToken: ct);

			if (site?.Id == null)
				return null;

			var drives = await _graphClient
				.Sites[site.Id]
				.Drives
				.GetAsync(cancellationToken: ct);

			var drive = drives?.Value?.FirstOrDefault(d =>
				string.Equals(d.Name, _options.DriveName, StringComparison.OrdinalIgnoreCase));

			return drive?.Id;
		}

		public Task EnsureFolderPathAsync(string driveId, string folderPath, CancellationToken ct)
			=> EnsureFolderHierarchyExistsAsync(driveId, folderPath, ct);

		public async Task<SharePointItem?> TryGetItemByPathAsync(string driveId, string itemPath, CancellationToken ct)
		{
			try
			{
				var item = await _graphClient
					.Drives[driveId]
					.Root
					.ItemWithPath(itemPath)
					.GetAsync(cancellationToken: ct);

				return item == null ? null : MapItem(driveId, item);
			}
			catch
			{
				return null;
			}
		}

		public async Task<IReadOnlyList<SharePointItem>> ListChildrenAsync(string driveId, string folderPath, CancellationToken ct)
		{
			DriveItemCollectionResponse? response;

			if (string.IsNullOrWhiteSpace(folderPath))
			{
				response = await _graphClient
					.Drives[driveId]
					.Items["root"]
					.Children
					.GetAsync(cancellationToken: ct);
			}
			else
			{
				response = await _graphClient
					.Drives[driveId]
					.Root
					.ItemWithPath(folderPath)
					.Children
					.GetAsync(cancellationToken: ct);
			}

			return response?.Value?.Select(i => MapItem(driveId, i)).ToList()
				?? new List<SharePointItem>();
		}

		public async Task DownloadFileAsync(string driveId, string itemId, string localFilePath, bool overwrite, CancellationToken ct)
		{
			if (File.Exists(localFilePath) && !overwrite)
				return;

			var parent = Path.GetDirectoryName(localFilePath);
			if (!string.IsNullOrWhiteSpace(parent))
				Directory.CreateDirectory(parent);

			using var content = await _graphClient
				.Drives[driveId]
				.Items[itemId]
				.Content
				.GetAsync(cancellationToken: ct);

			if (content == null)
				throw new InvalidOperationException($"No content returned for item '{itemId}'.");

			await using var target = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
			await content.CopyToAsync(target, ct);
		}

		public async Task UploadFileAsync(
			string driveId,
			string folderPath,
			string localFilePath,
			string targetFileName,
			bool overwrite,
			CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(driveId))
				throw new ArgumentNullException(nameof(driveId));
			if (string.IsNullOrWhiteSpace(localFilePath))
				throw new ArgumentNullException(nameof(localFilePath));
			if (string.IsNullOrWhiteSpace(targetFileName))
				throw new ArgumentNullException(nameof(targetFileName));

			var fileInfo = new FileInfo(localFilePath);
			if (!fileInfo.Exists)
				throw new FileNotFoundException("Local file not found.", localFilePath);

			var remotePath = string.IsNullOrWhiteSpace(folderPath)
				? targetFileName
				: $"{folderPath.TrimEnd('/', '\\')}/{targetFileName}";

			_logger.LogInformation(
				"Uploading file '{LocalFile}' to '{RemotePath}' in drive '{DriveId}'",
				localFilePath, remotePath, driveId);

			try
			{
				await UploadInternalAsync(driveId, remotePath, localFilePath, overwrite, ct);
			}
			catch (Exception ex) when (overwrite && IsTooManyMinorVersionsError(ex))
			{
				_logger.LogWarning(ex,
					"Upload blocked by SharePoint minor-version limit for '{RemotePath}'. Deleting existing remote file and retrying as a fresh upload.",
					remotePath);

				await DeleteRemoteFileIfExistsAsync(driveId, remotePath, ct);
				await WaitUntilRemoteFileDeletedAsync(driveId, remotePath, ct);
				await Task.Delay(1000, ct);

				await UploadInternalAsync(driveId, remotePath, localFilePath, overwrite: false, ct);
			}
		}

		private static SharePointItem MapItem(string driveId, DriveItem item)
		{
			var parentPath = item.ParentReference?.Path;
			var normalizedParent = parentPath;
			const string marker = "/root:";
			var idx = parentPath?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
			if (idx >= 0)
			{
				normalizedParent = parentPath![..].Substring(idx + marker.Length).Trim('/');
			}

			return new SharePointItem
			{
				DriveId = driveId,
				ItemId = item.Id ?? string.Empty,
				Name = item.Name ?? string.Empty,
				IsFolder = item.Folder != null,
				Size = item.Size,
				LastModifiedUtc = item.LastModifiedDateTime,
				ETag = item.ETag,
				WebUrl = item.WebUrl,
				ParentPath = normalizedParent,
				LastModifiedDateTime = item.LastModifiedDateTime?.UtcDateTime
			};
		}

		private async Task UploadInternalAsync(
			string driveId,
			string remotePath,
			string localFilePath,
			bool overwrite,
			CancellationToken ct)
		{
			var fileInfo = new FileInfo(localFilePath);

			if (fileInfo.Length <= LargeFileThreshold)
			{
				await UploadSmallFileAsync(driveId, remotePath, localFilePath, overwrite, ct);
			}
			else
			{
				await UploadLargeFileWithSessionAsync(driveId, remotePath, localFilePath, overwrite, ct);
			}
		}

		private static bool IsTooManyMinorVersionsError(Exception ex)
		{
			var text = ex.ToString();

			return text.Contains("Document has too many minor versions", StringComparison.OrdinalIgnoreCase)
				|| (text.Contains("\"code\":\"notAllowed\"", StringComparison.OrdinalIgnoreCase)
					&& text.Contains("minor versions", StringComparison.OrdinalIgnoreCase));
		}

		private async Task DeleteRemoteFileIfExistsAsync(
			string driveId,
			string remotePath,
			CancellationToken ct)
		{
			try
			{
				var existingItem = await _graphClient
					.Drives[driveId]
					.Root
					.ItemWithPath(remotePath)
					.GetAsync(cancellationToken: ct);

				if (existingItem?.Id == null)
					return;

				await _graphClient
					.Drives[driveId]
					.Items[existingItem.Id]
					.DeleteAsync(cancellationToken: ct);

				_logger.LogInformation("Deleted existing remote file '{RemotePath}' before retry upload.", remotePath);
			}
			catch
			{
				_logger.LogInformation("Remote file '{RemotePath}' was not found during delete retry flow.", remotePath);
			}
		}

		private async Task WaitUntilRemoteFileDeletedAsync(
			string driveId,
			string remotePath,
			CancellationToken ct)
		{
			for (int i = 0; i < 10; i++)
			{
				try
				{
					var item = await _graphClient
						.Drives[driveId]
						.Root
						.ItemWithPath(remotePath)
						.GetAsync(cancellationToken: ct);

					if (item == null)
						return;
				}
				catch
				{
					return;
				}

				await Task.Delay(500, ct);
			}
		}

		private async Task UploadSmallFileAsync(
			string driveId,
			string remotePath,
			string localFilePath,
			bool overwrite,
			CancellationToken ct)
		{
			await EnsureFolderHierarchyExistsAsync(
				driveId,
				Path.GetDirectoryName(remotePath)?.Replace("\\", "/"),
				ct);

			await using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

			var request = _graphClient
				.Drives[driveId]
				.Root
				.ItemWithPath(remotePath)
				.Content
				.ToPutRequestInformation(fs);

			await _graphClient.RequestAdapter.SendPrimitiveAsync<Stream>(request, cancellationToken: ct);
		}

		private async Task UploadLargeFileWithSessionAsync(
			string driveId,
			string remotePath,
			string localFilePath,
			bool overwrite,
			CancellationToken ct)
		{
			var fileInfo = new FileInfo(localFilePath);
			var totalSize = fileInfo.Length;

			await EnsureFolderHierarchyExistsAsync(
				driveId,
				Path.GetDirectoryName(remotePath)?.Replace("\\", "/"),
				ct);

			var uploadSession = await CreateUploadSessionInternalAsync(driveId, remotePath, overwrite, ct);
			var uploadUrl = uploadSession.UploadUrl;

			await using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

			long offset = 0;
			int retryCount = 0;

			while (offset < totalSize)
			{
				ct.ThrowIfCancellationRequested();

				if (uploadSession.ExpirationDateTime < DateTimeOffset.UtcNow.AddMinutes(2))
				{
					_logger.LogWarning(
						"Upload session for '{RemotePath}' is near expiration. Recreating session and restarting upload.",
						remotePath);

					uploadSession = await CreateUploadSessionInternalAsync(driveId, remotePath, overwrite, ct);
					uploadUrl = uploadSession.UploadUrl;
					offset = 0;
					fs.Seek(0, SeekOrigin.Begin);
				}

				var remaining = totalSize - offset;
				var bytesToRead = (int)Math.Min(ChunkSize, remaining);
				var buffer = new byte[bytesToRead];

				var read = await fs.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
				if (read == 0)
					break;

				var start = offset;
				var end = offset + read - 1;

				using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
				{
					Content = new ByteArrayContent(buffer, 0, read)
				};

				request.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {start}-{end}/{totalSize}");

				try
				{
					using var response = await _httpClient.SendAsync(
						request,
						HttpCompletionOption.ResponseContentRead,
						ct);

					if (response.StatusCode == HttpStatusCode.Created ||
						response.StatusCode == HttpStatusCode.OK)
					{
						_logger.LogInformation("Completed upload of '{RemotePath}'", remotePath);
						return;
					}

					if (response.StatusCode == HttpStatusCode.Accepted)
					{
						offset = end + 1;
						retryCount = 0;
						continue;
					}

					var body = await response.Content.ReadAsStringAsync(ct);
					throw new InvalidOperationException($"Upload failed: {response.StatusCode} - {body}");
				}
				catch (Exception ex)
				{
					retryCount++;

					if (retryCount > 3)
					{
						_logger.LogError(ex, "Failed uploading '{RemotePath}' after retries.", remotePath);
						throw new InvalidOperationException($"Failed uploading '{remotePath}' after retries.", ex);
					}

					_logger.LogWarning(
						ex,
						"Error uploading chunk for '{RemotePath}'. Retrying (attempt {Retry})",
						remotePath,
						retryCount);

					await Task.Delay(1500, ct);
					fs.Seek(offset, SeekOrigin.Begin);
				}
			}

			throw new InvalidOperationException($"Failed uploading '{remotePath}'. Upload loop exited unexpectedly.");
		}

		private async Task EnsureFolderHierarchyExistsAsync(
			string driveId,
			string? folderPath,
			CancellationToken ct)
		{
			if (string.IsNullOrWhiteSpace(folderPath))
				return;

			var segments = folderPath
				.Replace("\\", "/")
				.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

			var currentPath = string.Empty;

			foreach (var segment in segments)
			{
				currentPath = string.IsNullOrEmpty(currentPath)
					? segment
					: $"{currentPath}/{segment}";

				try
				{
					_ = await _graphClient
						.Drives[driveId]
						.Root
						.ItemWithPath(currentPath)
						.GetAsync(cancellationToken: ct);
				}
				catch
				{
					var parentPath = Path.GetDirectoryName(currentPath)?.Replace("\\", "/") ?? string.Empty;

					var folder = new DriveItem
					{
						Name = segment,
						Folder = new Folder(),
						AdditionalData = new Dictionary<string, object>
						{
							{ "@microsoft.graph.conflictBehavior", "replace" }
						}
					};

					if (string.IsNullOrWhiteSpace(parentPath))
					{
						await _graphClient
							.Drives[driveId]
							.Items["root"]
							.Children
							.PostAsync(folder, cancellationToken: ct);
					}
					else
					{
						await _graphClient
							.Drives[driveId]
							.Root
							.ItemWithPath(parentPath)
							.Children
							.PostAsync(folder, cancellationToken: ct);
					}
				}
			}
		}

		private async Task<UploadSession> CreateUploadSessionInternalAsync(
			string driveId,
			string remotePath,
			bool overwrite,
			CancellationToken ct)
		{
			var body = new CreateUploadSessionPostRequestBody
			{
				Item = new DriveItemUploadableProperties
				{
					AdditionalData = new Dictionary<string, object>
					{
						{ "@microsoft.graph.conflictBehavior", overwrite ? "replace" : "fail" }
					}
				}
			};

			var session = await _graphClient
				.Drives[driveId]
				.Root
				.ItemWithPath(remotePath)
				.CreateUploadSession
				.PostAsync(body, cancellationToken: ct);

			if (session == null || string.IsNullOrWhiteSpace(session.UploadUrl))
				throw new InvalidOperationException($"Failed to create upload session for '{remotePath}'.");

			return session;
		}
	}
}