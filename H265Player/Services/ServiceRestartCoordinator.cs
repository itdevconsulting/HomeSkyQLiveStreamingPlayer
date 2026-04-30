using System.Threading;
using H265Player.Models;
using Microsoft.Extensions.Hosting;

namespace H265Player.Services;

public sealed class ServiceRestartCoordinator
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServiceRestartCoordinator> _logger;
    private int _restartScheduled;

    public ServiceRestartCoordinator(
        IHostApplicationLifetime lifetime,
        ILogger<ServiceRestartCoordinator> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    public ServiceRestartStatusResponse GetStatus()
    {
        var canSelfRestart = IsSystemdManagedService();
        var restartScheduled = Volatile.Read(ref _restartScheduled) == 1;
        var serviceMode = GetServiceMode();

        if (restartScheduled)
        {
            return new ServiceRestartStatusResponse(
                canSelfRestart,
                true,
                serviceMode,
                "Restart requested. The service should reconnect within a few seconds.");
        }

        if (canSelfRestart)
        {
            return new ServiceRestartStatusResponse(
                true,
                false,
                serviceMode,
                "Self-restart is available for the installed Linux systemd service.");
        }

        return new ServiceRestartStatusResponse(
            false,
            false,
            serviceMode,
            "Self-restart is only enabled when the app is running under the installed Linux systemd service.");
    }

    public ServiceRestartStatusResponse ScheduleRestart()
    {
        if (!IsSystemdManagedService())
        {
            return GetStatus();
        }

        if (Interlocked.Exchange(ref _restartScheduled, 1) == 1)
        {
            return GetStatus();
        }

        _logger.LogWarning("Streaming service restart requested from the setup UI. Application stop has been scheduled.");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200));
                _lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _restartScheduled, 0);
                _logger.LogError(ex, "Failed to schedule a graceful application stop for self-restart.");
            }
        });

        return GetStatus();
    }

    private static bool IsSystemdManagedService() =>
        OperatingSystem.IsLinux() &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVOCATION_ID"));

    private static string GetServiceMode()
    {
        if (IsSystemdManagedService())
        {
            return "systemd";
        }

        if (OperatingSystem.IsWindows())
        {
            return "desktop";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux-manual";
        }

        return "manual";
    }
}
