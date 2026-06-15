using FolderRetentionCleanupWorker;
using FolderRetentionCleanupWorker.Models;
using FolderRetentionCleanupWorker.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(AppContext.BaseDirectory, "Logs", "folder-cleanup-worker-.txt"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .CreateLogger();

try
{
    Log.Information("Starting Folder Retention Cleanup Worker");

    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "Folder Retention Cleanup Worker";
    });

    builder.Services.AddSerilog();

    builder.Services.Configure<FolderCleanupSettings>(
        builder.Configuration.GetSection(FolderCleanupSettings.SectionName));

    builder.Services.AddSingleton<IFolderCleanupService, FolderCleanupService>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Folder Retention Cleanup Worker terminated unexpectedly");
}
finally
{
    Log.Information("Stopped Folder Retention Cleanup Worker");
    await Log.CloseAndFlushAsync();
}
