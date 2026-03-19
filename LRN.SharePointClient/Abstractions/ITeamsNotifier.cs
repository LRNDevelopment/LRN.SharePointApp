namespace LRN.SharePointClient.Abstractions;

public interface ITeamsNotifier
{
    Task SendAsync(string title, string message, CancellationToken ct = default);
}
