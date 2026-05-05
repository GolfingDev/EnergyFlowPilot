namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one validated direct Decision Engine result for the current control cycle.
/// </summary>
public sealed record CurrentBatteryDecisionResult
{
    /// <summary>
    /// Validates the direct decision result before API delivery or persistence.
    /// </summary>
    public CurrentBatteryDecisionResult(
        DateTimeOffset decidedAtUtc,
        DateTimeOffset validFromUtc,
        DateTimeOffset validToUtc,
        CurrentBatteryDecision decision,
        BatteryState batteryState,
        CurrentSiteTelemetry siteTelemetry,
        decimal? tibberPricePerKwh,
        string? tibberPriceCurrency,
        IReadOnlyList<BatteryDecisionReason> reasons,
        string inputSummaryJson)
    {
        if (decidedAtUtc.Offset != TimeSpan.Zero || validFromUtc.Offset != TimeSpan.Zero || validToUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Alle Zeitpunkte der Direktentscheidung muessen in UTC angegeben sein.");
        }

        if (validToUtc <= validFromUtc)
        {
            throw new ArgumentException("Das Gueltigkeitsende der Direktentscheidung muss nach dem Start liegen.", nameof(validToUtc));
        }

        if (reasons.Count == 0)
        {
            throw new ArgumentException("Eine Direktentscheidung braucht mindestens eine Begruendung.", nameof(reasons));
        }

        if (string.IsNullOrWhiteSpace(inputSummaryJson))
        {
            throw new ArgumentException("Die Eingabezusammenfassung der Direktentscheidung muss angegeben sein.", nameof(inputSummaryJson));
        }

        DecidedAtUtc = decidedAtUtc;
        ValidFromUtc = validFromUtc;
        ValidToUtc = validToUtc;
        Decision = decision;
        BatteryState = batteryState;
        SiteTelemetry = siteTelemetry;
        TibberPricePerKwh = tibberPricePerKwh;
        TibberPriceCurrency = tibberPriceCurrency;
        Reasons = reasons;
        InputSummaryJson = inputSummaryJson;
    }

    public DateTimeOffset DecidedAtUtc { get; }

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ValidToUtc { get; }

    public CurrentBatteryDecision Decision { get; }

    public BatteryState BatteryState { get; }

    public CurrentSiteTelemetry SiteTelemetry { get; }

    public decimal? TibberPricePerKwh { get; }

    public string? TibberPriceCurrency { get; }

    public IReadOnlyList<BatteryDecisionReason> Reasons { get; }

    public string InputSummaryJson { get; }
}
