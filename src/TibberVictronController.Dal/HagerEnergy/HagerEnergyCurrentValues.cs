namespace TibberVictronController.Dal.HagerEnergy;

public sealed record HagerEnergyCurrentValues(
    decimal GridImportWatts,
    decimal PvProductionWatts,
    decimal BatterySocPercent,
    decimal? BatteryPowerWatts,
    DateTimeOffset MeasuredAtUtc)
{
    /// <summary>
    /// Gets the unmodified API response; kept for diagnostics because the field names of the API are not documented here.
    /// </summary>
    public string? RawJson { get; init; }
}

public sealed record HagerEnergyMeasuredValue(
    decimal Value,
    DateTimeOffset MeasuredAtUtc);
