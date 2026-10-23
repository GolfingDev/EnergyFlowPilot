namespace TibberVictronController.Business.Models;

/// <summary>Local device readings. Positive battery power means charging; capacity includes the reserve.</summary>
public sealed record E3dcLocalTelemetry(
    DateTimeOffset MeasuredAtUtc, decimal SocPercent, int BatteryPowerWatts,
    decimal UsableCapacityKwh, decimal ReserveKwh, int MaximumChargeWatts,
    int MaximumDischargeWatts, int EffectiveChargeLimitWatts, int EffectiveDischargeLimitWatts,
    int OperatingMode, bool WeatherRegulationEnabled);
