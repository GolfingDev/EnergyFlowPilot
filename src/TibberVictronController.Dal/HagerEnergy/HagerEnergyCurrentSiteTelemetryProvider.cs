using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.HagerEnergy;

/// <summary>
/// Provides live site telemetry from the Hager Energy API.
/// </summary>
public sealed class HagerEnergyCurrentSiteTelemetryProvider : ICurrentSiteTelemetryProvider
{
    private readonly HagerEnergyApiClient apiClient;

    public HagerEnergyCurrentSiteTelemetryProvider(HagerEnergyApiClient apiClient)
    {
        this.apiClient = apiClient;
    }

    public async Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var currentValues = await apiClient.GetCurrentValuesAsync(cancellationToken);

        return new CurrentSiteTelemetry(
            DecimalToInt(currentValues.GridImportWatts),
            DecimalToInt(currentValues.PvProductionWatts),
            currentValues.MeasuredAtUtc);
    }

    public async Task<(int GridImportWatts, DateTimeOffset MeasuredAtUtc)> GetGridImportWattsAsync(CancellationToken cancellationToken = default)
    {
        var value = await apiClient.GetGridImportWattsAsync(cancellationToken);

        return (DecimalToInt(value.Value), value.MeasuredAtUtc);
    }

    public async Task<(int PvProductionWatts, DateTimeOffset MeasuredAtUtc)> GetPvProductionWattsAsync(CancellationToken cancellationToken = default)
    {
        var value = await apiClient.GetPvProductionWattsAsync(cancellationToken);

        return (DecimalToInt(value.Value), value.MeasuredAtUtc);
    }

    private static int DecimalToInt(decimal value)
    {
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
