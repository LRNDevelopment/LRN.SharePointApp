namespace LRN.SharePointClient.Models;

public sealed class SharePointItem
{
    public string DriveId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public long? Size { get; set; }
    public DateTimeOffset? LastModifiedUtc { get; set; }
    public string? ETag { get; set; }
}
