using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Provides battery state of charge from the Hager Energy API.
/// </summary>
public sealed class HagerEnergyBatteryStateProvider : IBatteryStateProvider
{
    private readonly HagerEnergyApiClient apiClient;

    public HagerEnergyBatteryStateProvider(HagerEnergyApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        var stateOfCharge = await apiClient.GetBatterySocPercentAsync(cancellationToken);

        return new BatteryState(
            stateOfCharge.Value,
            stateOfCharge.MeasuredAtUtc);
    }
}
