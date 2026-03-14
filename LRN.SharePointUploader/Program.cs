using LRN.DataLibrary.Registration;
using LRN.SharePointClient;
using LRN.SharePointClient.Models;
using LRN.SharePointClient.Sync;
using LRN.SharePointUploader.ProcessLogging;
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

        // Force-load appsettings.json
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        // Windows Service
        builder.Services.AddWindowsService(o => o.ServiceName = "LRN SharePoint Uploader");

        // Options
        var opt = builder.Configuration.GetSection("SharePointUploader")
            .Get<UploaderWorkerOptions>() ?? new UploaderWorkerOptions();

        var uploadPaths = builder.Configuration.GetSection("UploadPaths")
            .Get<List<UploadPathItem>>() ?? new List<UploadPathItem>();

        opt.UploadPaths = uploadPaths;
        builder.Services.AddSingleton(Options.Create(opt));

        // SharePoint client
        builder.Services.AddLrnSharePointClient(builder.Configuration);
        builder.Services.AddSingleton<FolderSyncEngine>();

        // Step log
        var logOpt = builder.Configuration.GetSection("LrnStepLog")
            .Get<LrnStepLogOptions>() ?? new LrnStepLogOptions();

        builder.Services.AddSingleton(Options.Create(logOpt));

        if (!string.IsNullOrWhiteSpace(logOpt.ConnectionString))
            builder.Configuration["DefaultConnection:LrnLogDb"] = logOpt.ConnectionString;

        if (logOpt.Enabled)
        {
            builder.Services.AddLrnLogDataLibrary(builder.Configuration);
            builder.Services.AddScoped<LrnProcessLogger>();
        }

        // Worker
        builder.Services.AddHostedService<SharePointUploaderWorker>();

        var host = builder.Build();
        await host.RunAsync();
    }
}