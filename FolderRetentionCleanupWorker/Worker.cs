using FolderRetentionCleanupWorker.Models;
using FolderRetentionCleanupWorker.Services;
using Microsoft.Extensions.Options;

namespace FolderRetentionCleanupWorker;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IFolderCleanupService _folderCleanupService;
    private readonly IOptionsMonitor<FolderCleanupSettings> _settings;

    public Worker(
        ILogger<Worker> logger,
        IFolderCleanupService folderCleanupService,
        IOptionsMonitor<FolderCleanupSettings> settings)
    {
        _logger = logger;
        _folderCleanupService = folderCleanupService;
        _settings = settings;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker start requested");
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = _settings.CurrentValue;
            var delay = GetScanInterval(settings);

            try
            {
                if (settings.Enabled)
                {
                    await _folderCleanupService.CleanupAsync(stoppingToken);
                }
                else
                {
                    _logger.LogInformation("Folder cleanup is disabled by configuration");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected cleanup cycle error");
            }

            try
            {
                _logger.LogInformation("Next cleanup scan scheduled in {ScanIntervalMinutes} minute(s)", delay.TotalMinutes);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Worker stopping");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker stop requested");
        return base.StopAsync(cancellationToken);
    }

    private static TimeSpan GetScanInterval(FolderCleanupSettings settings)
    {
        var minutes = settings.ScanIntervalMinutes > 0 ? settings.ScanIntervalMinutes : 60;
        return TimeSpan.FromMinutes(minutes);
    }
}
