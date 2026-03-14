namespace LRN.SharePointClient.Utils;

public static class SharePointFolderLinkParser
{
    /// <summary>
    /// Accepts either a drive-relative folder path (e.g. "10. Automation/LRN-Output/Averages")
    /// OR a SharePoint folder link like:
    ///   https://tenant.sharepoint.com/sites/Site/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2FSite%2FShared%20Documents%2F10%2E%20Automation%2F...
    /// Returns drive-relative path under the document library.
    /// </summary>
    public static string ToDriveRelativeFolderPath(string sharePointFolderPathOrLink)
    {
        if (string.IsNullOrWhiteSpace(sharePointFolderPathOrLink))
            return "";

        // Already a path (no scheme)
        if (!Uri.TryCreate(sharePointFolderPathOrLink, UriKind.Absolute, out var uri))
            return NormalizePath(sharePointFolderPathOrLink);

        // Link might be a folder-view URL (AllItems.aspx?id=...)
        var id = TryGetQueryParam(uri.Query, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            var decoded = Uri.UnescapeDataString(id);
            // expected: /sites/<site>/Shared Documents/<folder path>
            return DriveRelativeFromServerRelative(decoded);
        }

        // Fallback: try to detect "Shared Documents" segment from the path itself
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return DriveRelativeFromServerRelative(path);
    }

    private static string DriveRelativeFromServerRelative(string serverRelative)
    {
        if (string.IsNullOrWhiteSpace(serverRelative))
            return "";

        var p = serverRelative.Replace("\\", "/");

        // Remove leading host-relative part
        // Example: /sites/Site/Shared Documents/10. Automation/LRN-Output
        var marker = "/Shared Documents/";
        var idx = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var after = p[(idx + marker.Length)..];
            return NormalizePath(after);
        }

        // Some tenants use /Shared%20Documents/ in URLs
        marker = "/Shared%20Documents/";
        idx = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var after = p[(idx + marker.Length)..];
            return NormalizePath(after);
        }

        // If we can't find Shared Documents, return the raw serverRelative trimmed.
        return NormalizePath(p.TrimStart('/'));
    }

    private static string NormalizePath(string path)
    {
        var p = (path ?? "").Replace("\\", "/").Trim();
        p = p.Trim('/');
        // Collapse accidental duplicate slashes
        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);
        return p;
    }

    private static string? TryGetQueryParam(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            return null;

        var q = query;
        if (q.StartsWith("?", StringComparison.Ordinal))
            q = q[1..];

        var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var k = part[..eq];
            if (!string.Equals(Uri.UnescapeDataString(k), key, StringComparison.OrdinalIgnoreCase))
                continue;
            var v = part[(eq + 1)..];
            return Uri.UnescapeDataString(v.Replace('+', ' '));
        }
        return null;
    }
}
