using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Models;
using LRN.SharePointClient.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LRN.SharePointClient.Graph;

/// <summary>
/// Microsoft Graph implementation (app-only) for SharePoint document library operations.
/// </summary>
public sealed class GraphSharePointClient : ISharePointClient
{
	private readonly HttpClient _http;
	private readonly SharePointGraphOptions _opt;
	private readonly ILogger<GraphSharePointClient> _log;
	private readonly GraphTokenProvider _tokens;

	private string? _cachedSiteId;
	private string? _cachedDriveId;

	public GraphSharePointClient(HttpClient http, IOptions<SharePointGraphOptions> opt, ILogger<GraphSharePointClient> log)
	{
		_http = http;
		_opt = opt.Value;
		_log = log;
		_tokens = new GraphTokenProvider(http, opt, log);
	}

	public async Task<string?> TryResolveDriveIdAsync(CancellationToken ct)
	{
		if (!_opt.Enabled) return null;
		if (!string.IsNullOrWhiteSpace(_cachedDriveId)) return _cachedDriveId;

		var siteId = await TryResolveSiteIdAsync(ct);
		if (string.IsNullOrWhiteSpace(siteId)) return null;

		var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives?$select=id,name,driveType,webUrl";
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		await _tokens.AddAuthHeaderAsync(req, ct);

		using var resp = await _http.SendAsync(req, ct);
		var json = await resp.Content.ReadAsStringAsync(ct);

		if (!resp.IsSuccessStatusCode)
		{
			_log.LogError("Graph drives list failed ({Status}). Response: {Body}",
				(int)resp.StatusCode, Trunc(json, 800));
			return null;
		}

		using var doc = JsonDocument.Parse(json);

		foreach (var d in doc.RootElement.GetProperty("value").EnumerateArray())
		{
			var name = d.TryGetProperty("name", out var n) ? n.GetString() : null;
			var id = d.TryGetProperty("id", out var i) ? i.GetString() : null;

			_log.LogInformation("Available drive: Name='{Name}', Id='{Id}'", name, id);

			if (!string.IsNullOrWhiteSpace(id) &&
				string.Equals(name, _opt.DriveName, StringComparison.OrdinalIgnoreCase))
			{
				_cachedDriveId = id;
				_log.LogInformation("Resolved drive '{DriveName}' => {DriveId}", name, id);
				return id;
			}
		}

		throw new InvalidOperationException(
			$"Configured SharePoint drive '{_opt.DriveName}' was not found. " +
			"Do not fall back to the first drive.");
	}
	public async Task EnsureFolderPathAsync(string driveId, string folderPath, CancellationToken ct)
	{
		var clean = NormalizePath(folderPath);
		if (string.IsNullOrWhiteSpace(clean)) return;

		// Create missing folders segment-by-segment under drive root.
		var segs = clean.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var current = "";

		foreach (var seg in segs)
		{
			current = string.IsNullOrWhiteSpace(current) ? seg : current + "/" + seg;
			var exists = await TryGetItemByPathAsync(driveId, current, ct);
			if (exists != null && exists.IsFolder) continue;

			// Create folder under parent
			var parent = current.Contains('/') ? current[..current.LastIndexOf('/')].Trim('/') : "";
			var createUrl = string.IsNullOrWhiteSpace(parent)
				? $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/children"
				: $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{EncodePath(parent)}:/children";

			var payload = new Dictionary<string, object?>
			{
				["name"] = seg,
				["folder"] = new Dictionary<string, object?>(),   // {} in JSON
				["@microsoft.graph.conflictBehavior"] = "fail"
			};

			using var req = new HttpRequestMessage(HttpMethod.Post, createUrl)
			{
				Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
			};
			await _tokens.AddAuthHeaderAsync(req, ct);

			using var resp = await _http.SendAsync(req, ct);
			if (resp.IsSuccessStatusCode)
				continue;

			var body = await resp.Content.ReadAsStringAsync(ct);

			// If conflict, folder already exists -> ok
			if (resp.StatusCode == HttpStatusCode.Conflict)
				continue;

			_log.LogError("Failed creating folder '{Folder}'. Status={Status}. Body={Body}", current, (int)resp.StatusCode, Trunc(body, 800));
			throw new InvalidOperationException($"Failed creating SharePoint folder '{current}'.");
		}
	}

	public async Task<SharePointItem?> TryGetItemByPathAsync(string driveId, string itemPath, CancellationToken ct)
	{
		var clean = NormalizePath(itemPath);
		var url = string.IsNullOrWhiteSpace(clean)
			? $"https://graph.microsoft.com/v1.0/drives/{driveId}/root?$select=id,name,folder,file,eTag,lastModifiedDateTime,size,webUrl,parentReference"
			: $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{EncodePath(clean)}?$select=id,name,folder,file,eTag,lastModifiedDateTime,size,webUrl,parentReference";

		_log.LogInformation("Graph get item by path. DriveId={DriveId}, Path='{Path}', Url='{Url}'", driveId, clean, url);

		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		await _tokens.AddAuthHeaderAsync(req, ct);

		using var resp = await _http.SendAsync(req, ct);
		var json = await resp.Content.ReadAsStringAsync(ct);

		if (resp.StatusCode == HttpStatusCode.NotFound)
		{
			_log.LogInformation("Remote item NOT FOUND. DriveId={DriveId}, Path='{Path}'", driveId, clean);
			return null;
		}

		if (!resp.IsSuccessStatusCode)
		{
			_log.LogError(
				"Graph get item by path failed. Status={Status}, DriveId={DriveId}, Path='{Path}', Url='{Url}', Body={Body}",
				(int)resp.StatusCode, driveId, clean, url, Trunc(json, 1000));

			throw new InvalidOperationException(
				$"Graph get item by path failed. Status={(int)resp.StatusCode}, Path='{clean}'.");
		}

		using var doc = JsonDocument.Parse(json);
		var item = ToItem(doc.RootElement, driveId);

		_log.LogInformation("Remote item FOUND. DriveId={DriveId}, Path='{Path}', ItemId='{ItemId}', Name='{Name}'",
			driveId, clean, item.ItemId, item.Name);

		return item;
	}

	public async Task<IReadOnlyList<SharePointItem>> ListChildrenAsync(string driveId, string folderPath, CancellationToken ct)
	{
		var clean = NormalizePath(folderPath);
		var url = string.IsNullOrWhiteSpace(clean)
			? $"https://graph.microsoft.com/v1.0/drives/{driveId}/root/children?$select=id,name,folder,file,eTag,lastModifiedDateTime,size"
			: $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{EncodePath(clean)}:/children?$select=id,name,folder,file,eTag,lastModifiedDateTime,size";

		var items = new List<SharePointItem>();
		while (!string.IsNullOrWhiteSpace(url))
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			await _tokens.AddAuthHeaderAsync(req, ct);
			using var resp = await _http.SendAsync(req, ct);
			var json = await resp.Content.ReadAsStringAsync(ct);
			if (!resp.IsSuccessStatusCode)
			{
				_log.LogWarning("Graph list children failed ({Status}) for '{Folder}'. Body={Body}", (int)resp.StatusCode, clean, Trunc(json, 500));
				break;
			}

			using var doc = JsonDocument.Parse(json);
			foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
				items.Add(ToItem(e, driveId));

			url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
		}
		return items;
	}

	public async Task UploadFileAsync(string driveId, string folderPath, string localFilePath, string targetFileName, bool overwrite, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
			throw new FileNotFoundException("Local file not found", localFilePath);

		if (string.IsNullOrWhiteSpace(targetFileName))
			throw new InvalidOperationException("targetFileName must be supplied explicitly for SharePoint upload.");

		var folder = NormalizePath(folderPath);
		var fileName = targetFileName.Trim();
		var remotePath = string.IsNullOrWhiteSpace(folder) ? fileName : folder + "/" + fileName;

		_log.LogInformation(
			"Preparing SharePoint upload. LocalFile='{LocalFile}', TargetFileName='{TargetFileName}', RemotePath='{RemotePath}'",
			localFilePath, fileName, remotePath);

		if (!string.IsNullOrWhiteSpace(folder))
			await EnsureFolderPathAsync(driveId, folder, ct);

		var existing = await TryGetItemByPathAsync(driveId, remotePath, ct);

		_log.LogInformation(
			"Exists check. RemotePath='{RemotePath}', Exists={Exists}, ExistingName='{ExistingName}'",
			remotePath, existing != null, existing?.Name);

		if (existing != null && !overwrite)
			return;

		var fi = new FileInfo(localFilePath);
		if (fi.Length <= 4L * 1024L * 1024L)
		{
			var putUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{EncodePath(remotePath)}:/content";
			using var fs = File.OpenRead(localFilePath);
			using var req = new HttpRequestMessage(HttpMethod.Put, putUrl)
			{
				Content = new StreamContent(fs)
			};
			req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
			await _tokens.AddAuthHeaderAsync(req, ct);

			using var resp = await _http.SendAsync(req, ct);
			var body = await resp.Content.ReadAsStringAsync(ct);

			if (!resp.IsSuccessStatusCode)
				throw new InvalidOperationException($"Simple upload failed for '{remotePath}'. Body={body}");

			return;
		}

		await UploadLargeFileWithSessionAsync(driveId, remotePath, localFilePath, overwrite, ct);
	}

	public async Task DownloadFileAsync(string driveId, string itemId, string localFilePath, bool overwrite, CancellationToken ct)
	{
		if (File.Exists(localFilePath) && !overwrite)
			return;

		Directory.CreateDirectory(Path.GetDirectoryName(localFilePath) ?? ".");

		var url = $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{itemId}/content";
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		await _tokens.AddAuthHeaderAsync(req, ct);

		using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
		if (!resp.IsSuccessStatusCode)
		{
			var body = await resp.Content.ReadAsStringAsync(ct);
			_log.LogError("Graph download failed ({Status}) for item '{Id}'. Body={Body}", (int)resp.StatusCode, itemId, Trunc(body, 800));
			throw new InvalidOperationException($"Failed downloading item '{itemId}'.");
		}

		await using var outStream = File.Create(localFilePath);
		await resp.Content.CopyToAsync(outStream, ct);
	}

	private async Task<string?> TryResolveSiteIdAsync(CancellationToken ct)
	{
		if (!string.IsNullOrWhiteSpace(_cachedSiteId)) return _cachedSiteId;

		var host = !string.IsNullOrWhiteSpace(_opt.SiteHostName) ? _opt.SiteHostName : _opt.Hostname;

		if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(_opt.SitePath))
		{
			_log.LogError("SharePoint options missing SiteHostName or SitePath.");
			return null;
		}

		// Graph: /sites/{hostname}:/sites/{sitePath}
		var clean = _opt.SitePath.Trim();
		if (!clean.StartsWith("/", StringComparison.Ordinal))
			clean = "/" + clean;

		var url = $"https://graph.microsoft.com/v1.0/sites/{host}:{clean}?$select=id";
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		await _tokens.AddAuthHeaderAsync(req, ct);
		using var resp = await _http.SendAsync(req, ct);
		var json = await resp.Content.ReadAsStringAsync(ct);
		if (!resp.IsSuccessStatusCode)
		{
			_log.LogError("Graph site resolve failed ({Status}). Response: {Body}", (int)resp.StatusCode, Trunc(json, 800));
			return null;
		}

		using var doc = JsonDocument.Parse(json);
		_cachedSiteId = doc.RootElement.GetProperty("id").GetString();
		return _cachedSiteId;
	}

	private async Task UploadLargeFileWithSessionAsync(string driveId, string remotePath, string localFilePath, bool overwrite, CancellationToken ct)
	{
		var createUrl = $"https://graph.microsoft.com/v1.0/drives/{driveId}/root:/{EncodePath(remotePath)}:/createUploadSession";
		var payload = new
		{
			item = new Dictionary<string, object>
			{
				["@microsoft.graph.conflictBehavior"] = overwrite ? "replace" : "fail"
			}
		};

		using var createReq = new HttpRequestMessage(HttpMethod.Post, createUrl)
		{
			Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
		};
		await _tokens.AddAuthHeaderAsync(createReq, ct);

		using var createResp = await _http.SendAsync(createReq, ct);
		var createBody = await createResp.Content.ReadAsStringAsync(ct);

		if (!createResp.IsSuccessStatusCode)
		{
			_log.LogError("Create upload session failed ({Status}) for '{Path}'. Body={Body}",
				(int)createResp.StatusCode, remotePath, Trunc(createBody, 800));
			throw new InvalidOperationException($"Failed creating upload session for '{remotePath}'.");
		}

		using var createDoc = JsonDocument.Parse(createBody);
		var uploadUrl = createDoc.RootElement.GetProperty("uploadUrl").GetString();
		if (string.IsNullOrWhiteSpace(uploadUrl))
			throw new InvalidOperationException("Upload session did not return uploadUrl.");

		const int chunkSize = 10 * 1024 * 1024;
		var total = new FileInfo(localFilePath).Length;
		long offset = 0;

		await using var fs = File.OpenRead(localFilePath);
		var buffer = new byte[chunkSize];

		while (offset < total)
		{
			ct.ThrowIfCancellationRequested();

			var toRead = (int)Math.Min(chunkSize, total - offset);
			var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
			if (read <= 0) break;

			var start = offset;
			var end = offset + read - 1;

			using var chunkReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
			chunkReq.Content = new ByteArrayContent(buffer, 0, read);
			chunkReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
			chunkReq.Content.Headers.ContentLength = read;
			chunkReq.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);

			using var chunkResp = await _http.SendAsync(chunkReq, ct);
			var body = await chunkResp.Content.ReadAsStringAsync(ct);

			if (chunkResp.StatusCode == HttpStatusCode.Accepted)
			{
				_log.LogInformation(
					"Chunk accepted for '{Path}'. Range=bytes {Start}-{End}/{Total}. Body={Body}",
					remotePath, start, end, total, Trunc(body, 300));

				offset += read;
				continue;
			}

			if (chunkResp.StatusCode == HttpStatusCode.OK ||
				chunkResp.StatusCode == HttpStatusCode.Created)
			{
				string? itemId = null;
				string? itemName = null;
				string? webUrl = null;

				try
				{
					using var finalDoc = JsonDocument.Parse(body);
					itemId = finalDoc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
					itemName = finalDoc.RootElement.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
					webUrl = finalDoc.RootElement.TryGetProperty("webUrl", out var webEl) ? webEl.GetString() : null;
				}
				catch
				{
					// ignore parse issue, still log raw response
				}

				_log.LogInformation(
					"Large file upload completed. RemotePath='{RemotePath}', ItemId='{ItemId}', ItemName='{ItemName}', WebUrl='{WebUrl}'",
					remotePath, itemId, itemName, webUrl);

				offset += read;
				continue;
			}

			_log.LogError(
				"Chunk upload failed ({Status}) for '{Path}'. Content-Range=bytes {Start}-{End}/{Total}. Body={Body}",
				(int)chunkResp.StatusCode, remotePath, start, end, total, Trunc(body, 800));

			throw new InvalidOperationException($"Failed uploading '{remotePath}'.");
		}
	}

	private static SharePointItem ToItem(JsonElement e, string driveId)
	{
		var item = new SharePointItem
		{
			DriveId = driveId,
			ItemId = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
			Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
			IsFolder = e.TryGetProperty("folder", out _),
			ETag = e.TryGetProperty("eTag", out var et) ? et.GetString() : null,
			Size = e.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : null,
			WebUrl = e.TryGetProperty("webUrl", out var wu) ? wu.GetString() : null,
			ParentPath = e.TryGetProperty("parentReference", out var pr) && pr.TryGetProperty("path", out var pp)
				? pp.GetString()
				: null
		};

		if (e.TryGetProperty("lastModifiedDateTime", out var lm) && lm.ValueKind == JsonValueKind.String)
		{
			if (DateTimeOffset.TryParse(lm.GetString(), out var dto))
				item.LastModifiedUtc = dto.ToUniversalTime();
		}

		return item;
	}

	private static string NormalizePath(string path)
	{
		var p = (path ?? "").Replace("\\", "/").Trim().Trim('/');
		while (p.Contains("//", StringComparison.Ordinal))
			p = p.Replace("//", "/", StringComparison.Ordinal);
		return p;
	}

	private static string EncodePath(string path)
	{
		// Graph path segment encoding: encode each segment but keep slashes.
		var segs = NormalizePath(path)
			.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(Uri.EscapeDataString);
		return string.Join("/", segs);
	}

	private static string Trunc(string? s, int max)
	{
		if (string.IsNullOrEmpty(s)) return "";
		return s.Length <= max ? s : s[..max] + "...";
	}
}
