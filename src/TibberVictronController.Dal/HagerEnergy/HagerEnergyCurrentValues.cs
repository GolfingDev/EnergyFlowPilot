namespace TibberVictronController.Dal.HagerEnergy;

public sealed record HagerEnergyCurrentValues(
    decimal GridImportWatts,
    decimal PvProductionWatts,
    decimal BatterySocPercent,
    DateTimeOffset MeasuredAtUtc);

public sealed record HagerEnergyMeasuredValue(
    decimal Value,
    DateTimeOffset MeasuredAtUtc);
