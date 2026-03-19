namespace LRN.SharePointClient.Options;

public sealed class SharePointGraphOptions
{
    public bool Enabled { get; set; } = true;

    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>e.g. contoso.sharepoint.com</summary>
    public string SiteHostName { get; set; } = "";

    /// <summary>
    /// Backward-compatible alias used in some appsettings ("Hostname").
    /// If SiteHostName is empty, Hostname will be used.
    /// </summary>
    public string Hostname { get; set; } = "";

    /// <summary>e.g. /sites/MySite</summary>
    public string SitePath { get; set; } = "";

    /// <summary>Document library name, usually "Documents".</summary>
    public string DriveName { get; set; } = "Documents";
}
