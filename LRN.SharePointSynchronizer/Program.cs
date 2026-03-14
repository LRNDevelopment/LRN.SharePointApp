using LRN.SharePointClient;
using LRN.SharePointClient.Models;
using LRN.SharePointClient.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Windows Service
        builder.Services.AddWindowsService(o => o.ServiceName = "LRN SharePoint Synchronizer");

        // Logging
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
#if WINDOWS
        builder.Logging.AddEventLog();
#endif
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // Options
        var opt = builder.Configuration.GetSection("SharePointSynchronizer")
            .Get<SynchronizerWorkerOptions>() ?? new SynchronizerWorkerOptions();

        var uploadPaths = builder.Configuration.GetSection("UploadPaths")
            .Get<List<UploadPathItem>>() ?? new List<UploadPathItem>();

        opt.UploadPaths = uploadPaths;
        builder.Services.AddSingleton(Options.Create(opt));

        // SharePoint client
        builder.Services.AddLrnSharePointClient(builder.Configuration);
        builder.Services.AddSingleton<FolderSyncEngine>();

        // Worker
        builder.Services.AddHostedService<SharePointSynchronizerWorker>();

        var host = builder.Build();
        await host.RunAsync();
    }
}