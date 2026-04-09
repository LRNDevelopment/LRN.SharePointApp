using LRN.SharePointClient.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LRN.SharePointClient.Graph;

public class GraphTokenProvider
{
    private readonly HttpClient _http;
    private readonly SharePointGraphOptions _opt;
    private readonly ILogger _log;

    private string? _accessToken;
    private DateTimeOffset _expiresAtUtc;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GraphTokenProvider(HttpClient http, IOptions<SharePointGraphOptions> opt, ILogger log)
    {
        _http = http;
        _opt = opt.Value;
        _log = log;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _expiresAtUtc)
            return _accessToken!;

        await _gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _expiresAtUtc)
                return _accessToken!;

            var tokenUrl = $"https://login.microsoftonline.com/{_opt.TenantId}/oauth2/v2.0/token";
            var body = new Dictionary<string, string>
            {
                ["client_id"] = _opt.ClientId,
                ["client_secret"] = _opt.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new FormUrlEncodedContent(body)
            };

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("Graph token request failed ({Status}). Response: {Body}", (int)resp.StatusCode, Trunc(json, 800));
                throw new InvalidOperationException("Unable to acquire Microsoft Graph access token.");
            }

            using var doc = JsonDocument.Parse(json);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120)); // refresh 2min early

            if (string.IsNullOrWhiteSpace(_accessToken))
                throw new InvalidOperationException("Graph token response did not contain access_token.");

            return _accessToken!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAuthHeaderAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }
}
