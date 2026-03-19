using LRN.SharePointClient.Options;

namespace LRN.SharePointClient.Utils;

public static class SharePointWebLinkBuilder
{
    public static string? TryBuildFileUrl(SharePointGraphOptions? opt, string? driveRelativePath)
    {
        var host = opt?.SiteHostName;
        if (string.IsNullOrWhiteSpace(host))
            host = opt?.Hostname;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(opt?.SitePath) || string.IsNullOrWhiteSpace(driveRelativePath))
            return null;

        var sitePath = "/" + opt.SitePath.Trim().Trim('/');
        var cleanPath = driveRelativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(cleanPath))
            return null;

        var encoded = string.Join("/", cleanPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString));

        return $"https://{host}{sitePath}/Shared%20Documents/{encoded}";
    }
}
