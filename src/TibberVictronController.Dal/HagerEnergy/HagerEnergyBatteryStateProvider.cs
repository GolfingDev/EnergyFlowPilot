using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

public sealed class HagerEnergyBatteryStateProvider : IBatteryStateProvider
{
    private readonly HagerEnergyApiClient apiClient;

    public HagerEnergyBatteryStateProvider(HagerEnergyApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public async Task<BatteryState> GetCurrentBatteryStateAsync(CancellationToken cancellationToken = default)
    {
        var values = await apiClient.GetCurrentValuesAsync(cancellationToken);

        return new BatteryState(values.BatterySocPercent, values.MeasuredAtUtc);
    }
}
