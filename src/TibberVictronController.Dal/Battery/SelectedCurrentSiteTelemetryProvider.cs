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
    private readonly TelemetrySourceSelector sourceSelector;
    private readonly ResilientCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider;
    private readonly HagerEnergyCurrentSiteTelemetryProvider hagerEnergyCurrentSiteTelemetryProvider;

    public SelectedCurrentSiteTelemetryProvider(
        TelemetrySourceSelector sourceSelector,
        ResilientCurrentSiteTelemetryProvider victronCurrentSiteTelemetryProvider,
        HagerEnergyCurrentSiteTelemetryProvider hagerEnergyCurrentSiteTelemetryProvider)
    {
        this.sourceSelector = sourceSelector;
        this.victronCurrentSiteTelemetryProvider = victronCurrentSiteTelemetryProvider;
        this.hagerEnergyCurrentSiteTelemetryProvider = hagerEnergyCurrentSiteTelemetryProvider;
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
            victronTelemetry?.CurrentBatteryPowerWatts);
    }
}
