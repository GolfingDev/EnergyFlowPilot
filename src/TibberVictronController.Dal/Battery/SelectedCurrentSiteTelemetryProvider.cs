using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Routes live telemetry reads to the currently configured energy device integration.
/// </summary>
public sealed class SelectedCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private static readonly TimeSpan SupplementarySnapshotMaxAge = TimeSpan.FromMinutes(3);

    private readonly TelemetrySourceSelector sourceSelector;
    private readonly ResilientCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider;
    private readonly HagerEnergyCurrentSiteTelemetryProvider hagerEnergyCurrentSiteTelemetryProvider;
    private readonly HagerEnergyTelemetrySnapshotStore supplementarySnapshotStore;

    public SelectedCurrentSiteTelemetryProvider(
        TelemetrySourceSelector sourceSelector,
        ResilientCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider,
        HagerEnergyCurrentSiteTelemetryProvider hagerEnergyCurrentSiteTelemetryProvider,
        HagerEnergyTelemetrySnapshotStore supplementarySnapshotStore)
    {
        this.sourceSelector = sourceSelector;
        this.victronCurrentSiteTelemetryProvider = victronCurrentSiteTelemetryProvider;
        this.hagerEnergyCurrentSiteTelemetryProvider = hagerEnergyCurrentSiteTelemetryProvider;
        this.supplementarySnapshotStore = supplementarySnapshotStore;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var gridImportSource = await sourceSelector.GetGridImportSourceAsync(cancellationToken);
        var pvProductionSource = await sourceSelector.GetPvProductionSourceAsync(cancellationToken);

        var needsVictron = gridImportSource == TelemetrySourceSelector.VictronMqttProvider
            || pvProductionSource == TelemetrySourceSelector.VictronMqttProvider;
        var needsHager = gridImportSource == TelemetrySourceSelector.HagerEnergyApiProvider
            || pvProductionSource == TelemetrySourceSelector.HagerEnergyApiProvider;

        var victronTelemetry = needsVictron
            ? await victronCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken)
            : null;
        var hagerTelemetry = needsHager
            ? await hagerEnergyCurrentSiteTelemetryProvider.GetCurrentSiteTelemetryAsync(cancellationToken)
            : null;

        var gridImportWatts = gridImportSource switch
        {
            TelemetrySourceSelector.HagerEnergyApiProvider => hagerTelemetry!.CurrentGridImportWatts,
            TelemetrySourceSelector.VictronMqttProvider => victronTelemetry!.CurrentGridImportWatts,
            _ => throw new InvalidOperationException($"Die Telemetriequelle '{gridImportSource}' fuer den Netzbezug ist nicht bekannt.")
        };
        var pvProductionWatts = pvProductionSource switch
        {
            TelemetrySourceSelector.HagerEnergyApiProvider => hagerTelemetry!.CurrentPvProductionWatts,
            TelemetrySourceSelector.VictronMqttProvider => victronTelemetry!.CurrentPvProductionWatts,
            _ => throw new InvalidOperationException($"Die Telemetriequelle '{pvProductionSource}' fuer die PV-Leistung ist nicht bekannt.")
        };
        var measuredAtUtc = gridImportSource == TelemetrySourceSelector.HagerEnergyApiProvider
            ? hagerTelemetry!.MeasuredAtUtc
            : victronTelemetry!.MeasuredAtUtc;

        return new CurrentSiteTelemetry(
            gridImportWatts,
            pvProductionWatts,
            measuredAtUtc,
            victronTelemetry?.CurrentBatteryPowerWatts)
        {
            SupplementaryBatteryChargeFromSiteWatts = CalculateSupplementaryBatteryChargeFromSiteWatts(
                supplementarySnapshotStore.Current,
                DateTimeOffset.UtcNow),
            SupplementaryBatteryDischargeWatts = CalculateSupplementaryBatteryDischargeWatts(
                supplementarySnapshotStore.Current,
                DateTimeOffset.UtcNow)
        };
    }

    /// <summary>
    /// Returns how much the supplementary storage currently discharges; 0 when no fresh value exists.
    /// </summary>
    public static int CalculateSupplementaryBatteryDischargeWatts(
        HagerEnergyTelemetrySnapshot? snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot?.BatteryPowerWatts is not { } batteryPowerWatts ||
            nowUtc - snapshot.LastSuccessfulPollAtUtc > SupplementarySnapshotMaxAge)
        {
            return 0;
        }

        return Math.Max(0, -batteryPowerWatts);
    }

    /// <summary>
    /// Returns how much the supplementary storage charges beyond its own PV production, i.e. from the site or the controlled battery.
    /// </summary>
    public static int CalculateSupplementaryBatteryChargeFromSiteWatts(
        HagerEnergyTelemetrySnapshot? snapshot,
        DateTimeOffset nowUtc)
    {
        if (snapshot?.BatteryPowerWatts is not { } batteryPowerWatts ||
            nowUtc - snapshot.LastSuccessfulPollAtUtc > SupplementarySnapshotMaxAge)
        {
            return 0;
        }

        // Positive E3/DC battery power means charging; charging covered by its own PV is not taken from the site.
        return Math.Max(0, batteryPowerWatts - Math.Max(0, snapshot.PvProductionWatts));
    }
}
