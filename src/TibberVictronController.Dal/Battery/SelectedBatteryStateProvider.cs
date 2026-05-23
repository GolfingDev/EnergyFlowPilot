using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Dal.Battery;

/// <summary>
/// Routes battery state reads to the currently configured energy device integration.
/// </summary>
public sealed class SelectedBatteryStateProvider : IBatteryStateProvider
{
    private readonly TelemetrySourceSelector sourceSelector;
    private readonly ResilientBatteryStateProvider victronBatteryStateProvider;
    private readonly HagerEnergyBatteryStateProvider hagerEnergyBatteryStateProvider;

    public SelectedBatteryStateProvider(
        TelemetrySourceSelector sourceSelector,
        ResilientBatteryStateProvider victronBatteryStateProvider,
        HagerEnergyBatteryStateProvider hagerEnergyBatteryStateProvider)
    {
        this.sourceSelector = sourceSelector;
        this.victronBatteryStateProvider = victronBatteryStateProvider;
        this.hagerEnergyBatteryStateProvider = hagerEnergyBatteryStateProvider;
    }

    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        var provider = await sourceSelector.GetBatterySocSourceAsync(cancellationToken);

        return provider switch
        {
            TelemetrySourceSelector.HagerEnergyApiProvider => await hagerEnergyBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken),
            TelemetrySourceSelector.VictronMqttProvider => await victronBatteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken),
            _ => throw new InvalidOperationException($"Die Telemetriequelle '{provider}' fuer den Akku-SoC ist nicht bekannt.")
        };
    }
}
