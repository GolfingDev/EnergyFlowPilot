using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Periodically materializes measured live telemetry into durable battery savings day summaries.
/// </summary>
public sealed class BatterySavingsAccountingBackgroundService : BackgroundService
{
    private const int RecentDayCount = 2;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<BatterySavingsAccountingBackgroundService> logger;

    public BatterySavingsAccountingBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<BatterySavingsAccountingBackgroundService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteSingleCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Batterie-Ersparnis konnte in diesem Zyklus nicht aus Live-Daten aktualisiert werden.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    public async Task ExecuteSingleCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var accountingService = scope.ServiceProvider.GetRequiredService<IBatterySavingsAccountingService>();

        await accountingService.RefreshRecentDaysAsync(RecentDayCount, "EUR", cancellationToken);
    }
}
