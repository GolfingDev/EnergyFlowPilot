using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

public sealed class HagerEnergyCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly HagerEnergyApiClient apiClient;

    public HagerEnergyCurrentSiteTelemetryProvider(HagerEnergyApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var values = await apiClient.GetCurrentValuesAsync(cancellationToken);

        return new CurrentSiteTelemetry(
            (int)Math.Round(values.GridImportWatts, MidpointRounding.AwayFromZero),
            (int)Math.Round(values.PvProductionWatts, MidpointRounding.AwayFromZero),
            values.MeasuredAtUtc);
    }
}
