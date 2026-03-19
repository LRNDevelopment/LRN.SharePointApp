namespace LRN.SharePointClient.Options;

public sealed class TeamsNotificationOptions
{
    public bool Enabled { get; set; } = false;
    public string WebhookUrl { get; set; } = "";
}
