using System.Diagnostics.CodeAnalysis;

namespace MaichessMatchManagerService.Services;

[ExcludeFromCodeCoverage]
internal sealed partial class TimeoutWatchdog(MatchService matchService, ILogger<TimeoutWatchdog> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await matchService.EnforceTimeoutsAsync(stoppingToken);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogEnforcementFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Timeout enforcement failed")]
    private static partial void LogEnforcementFailed(ILogger logger, Exception ex);
}
