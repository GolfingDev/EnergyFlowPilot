using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Api.Configuration;

public interface ISupplementaryGridChargeSafety
{
    Task<BatteryDecisionReason?> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Rechecks the current E3/DC snapshot at the hardware boundary, including between decision cycles.</summary>
public sealed class SupplementaryGridChargeSafety(
    HagerEnergyTelemetrySnapshotStore snapshots, DatabaseHagerEnergySettingsProvider settings,
    SupplementaryGridChargeGuard guard, IUtcClock clock,
    ILogger<SupplementaryGridChargeSafety> logger) : ISupplementaryGridChargeSafety
{
    public async Task<BatteryDecisionReason?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = snapshots.Current;
            var required = snapshot is not null || await settings.IsConfiguredAsync(cancellationToken);
            return guard.Evaluate(SelectedCurrentSiteTelemetryProvider.WithSupplementaryTelemetry(
                new CurrentSiteTelemetry(0, 0, clock.UtcNow), snapshot, required), clock.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "E3/DC-Transfersperre konnte nicht geprueft werden. Netzladung wird sicherheitshalber gestoppt.");
            return guard.Evaluate(new CurrentSiteTelemetry(0, 0, clock.UtcNow)
                { SupplementaryBatteryMonitoringRequired = true }, clock.UtcNow);
        }
    }
}
