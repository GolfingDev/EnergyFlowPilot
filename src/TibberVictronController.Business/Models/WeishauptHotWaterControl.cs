namespace TibberVictronController.Business.Models;

public sealed record WeishauptHotWaterSettings
{
    public bool IsEnabled { get; init; }
    public decimal CheapPricePerKwh { get; init; } = 0.20m;
    public int PvSurplusWatts { get; init; } = 2000;
    public decimal MinimumPriceAdvantagePerKwh { get; init; } = 0.03m;
    public decimal ExtraEnergyPercent { get; init; } = 15m;
    public int DemandHourLocal { get; init; } = 18;
    public decimal InitialHoldHours { get; init; } = 6m;
    public int InitialChargeMinutes { get; init; } = 60;
}

/// <summary>Persisted ownership of a temporary normal-setpoint change, including crash recovery.</summary>
public sealed record WeishauptHotWaterState
{
    public decimal? OriginalCelsius { get; init; }
    public decimal? RaisedCelsius { get; init; }
    public DateTimeOffset? RaisedAtUtc { get; init; }
    public DateTimeOffset NextBoostAtUtc { get; init; } = DateTimeOffset.UnixEpoch;
    public string? DeviceIdentity { get; init; }
    public bool Suspended { get; init; }
    public DateTimeOffset? BoostUntilUtc { get; init; }
    public WeishauptHotWaterPricePlan? LastPlan { get; init; }
    public IReadOnlyList<WeishauptHotWaterEpisode> Episodes { get; init; } = [];
}

public sealed record WeishauptHotWaterReading(string DeviceIdentity, decimal NormalSetPointCelsius);

public sealed record WeishauptHotWaterPricePlan(
    DateTimeOffset NeedByUtc,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    decimal? WindowPricePerKwh,
    decimal? ReferencePricePerKwh,
    decimal? EstimatedAdvantagePerKwh,
    decimal HoldHours,
    int ChargeMinutes,
    string Reason);

public sealed record WeishauptHotWaterBoostContext(
    WeishauptHotWaterPricePlan Plan, string Trigger, bool TargetReached = false,
    decimal? LatestTemperatureCelsius = null);

public sealed record WeishauptHotWaterEpisode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required decimal OriginalCelsius { get; init; }
    public required decimal RaisedCelsius { get; init; }
    public required WeishauptHotWaterPricePlan Plan { get; init; }
    public required string Trigger { get; init; }
    public bool WriteConfirmed { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public bool Interrupted { get; init; }
}

public sealed record WeishauptHotWaterEpisodeAnalysis(
    WeishauptHotWaterEpisode Episode,
    DateTimeOffset? TargetReachedAtUtc,
    DateTimeOffset? NextHeatingAtUtc,
    DateTimeOffset? ReserveLostAtUtc,
    decimal? TemperatureAtNeedCelsius,
    bool? BridgedToNeed,
    bool HasDataGaps,
    string Assessment);
