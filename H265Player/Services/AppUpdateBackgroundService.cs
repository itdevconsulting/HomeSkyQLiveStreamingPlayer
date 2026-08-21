namespace H265Player.Services;

public sealed class AppUpdateBackgroundService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly AppUpdateService _updateService;
    private readonly LocalSetupStore _setupStore;
    private readonly ILogger<AppUpdateBackgroundService> _logger;

    public AppUpdateBackgroundService(
        AppUpdateService updateService,
        LocalSetupStore setupStore,
        ILogger<AppUpdateBackgroundService> logger)
    {
        _updateService = updateService;
        _setupStore = setupStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_setupStore.Get().AutoUpdateEnabled)
                {
                    var status = await _updateService.GetStatusAsync(forceRefresh: true, stoppingToken);
                    if (status is { CanApply: true, UpdateAvailable: true, UpdateQueued: false })
                    {
                        _logger.LogInformation("Automatic update is enabled and a newer GitHub commit is available.");
                        await _updateService.RequestUpdateAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic GitHub update check failed.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
