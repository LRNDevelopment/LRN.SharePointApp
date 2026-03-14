using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Graph;
using LRN.SharePointClient.Notifications;
using LRN.SharePointClient.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LRN.SharePointClient;

public static class SharePointClientRegistration
{
    /// <summary>
    /// Registers GraphSharePointClient and binds options.
    /// For backward compatibility with your existing appsettings, this binds from:
    ///   - BillingFrequency:SharePoint
    ///   - OR MasterFileProcessor:SharePoint
    ///   - OR SharePoint
    /// </summary>
    public static IServiceCollection AddLrnSharePointClient(this IServiceCollection services, IConfiguration config)
    {
        var spSection = config.GetSection("BillingFrequency").GetSection("SharePoint");
        if (!spSection.Exists())
            spSection = config.GetSection("MasterFileProcessor").GetSection("SharePoint");
        if (!spSection.Exists())
            spSection = config.GetSection("SharePoint");

        services.Configure<SharePointGraphOptions>(spSection);
        services.Configure<TeamsNotificationOptions>(config.GetSection("TeamsNotification"));

        services.AddHttpClient<GraphSharePointClient>();
        services.AddSingleton<ISharePointClient, GraphSharePointClient>();

        services.AddHttpClient<ITeamsNotifier, TeamsWebhookNotifier>();
        return services;
    }
}
