using Azure.Identity;
using LRN.SharePointClient.Abstractions;
using LRN.SharePointClient.Graph;
using LRN.SharePointClient.Notifications;
using LRN.SharePointClient.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace LRN.SharePointClient;

public static class SharePointClientRegistration
{
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

		services.AddSingleton<GraphServiceClient>(sp =>
		{
			var options = sp.GetRequiredService<IOptions<SharePointGraphOptions>>().Value;

			var credential = new ClientSecretCredential(
				options.TenantId,
				options.ClientId,
				options.ClientSecret);

			var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
			var httpClient = httpClientFactory.CreateClient();

			return new GraphServiceClient(
				httpClient,
				credential,
				new[] { "https://graph.microsoft.com/.default" });
		});

		services.AddTransient<ISharePointClient, GraphSharePointClient>();

		services.AddHttpClient<ITeamsNotifier, TeamsWebhookNotifier>();

		return services;
	}
}