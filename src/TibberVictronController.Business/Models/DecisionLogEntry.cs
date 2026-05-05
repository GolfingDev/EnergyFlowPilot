namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents one persisted realtime Decision Engine decision.
/// </summary>
public sealed record DecisionLogEntry
{
    /// <summary>
    /// Validates the persisted decision log contract before the DAL stores it.
    /// </summary>
    public DecisionLogEntry(
        Guid id,
        DateTimeOffset decidedAtUtc,
        DateTimeOffset validFromUtc,
        DateTimeOffset validToUtc,
        CurrentBatteryDecision decision,
        decimal? stateOfChargePercent,
        decimal? tibberPricePerKwh,
        string? tibberPriceCurrency,
        int? gridImportWatts,
        int? gridExportWatts,
        string inputSummaryJson,
        IReadOnlyList<BatteryDecisionReason> reasons)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Die Decision-Log-ID darf nicht leer sein.", nameof(id));
        }

        if (decidedAtUtc.Offset != TimeSpan.Zero || validFromUtc.Offset != TimeSpan.Zero || validToUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Alle Decision-Log-Zeitpunkte muessen in UTC angegeben sein.");
        }

        if (validToUtc <= validFromUtc)
        {
            throw new ArgumentException("Das Decision-Log-Gueltigkeitsende muss nach dem Start liegen.", nameof(validToUtc));
        }

        if (string.IsNullOrWhiteSpace(inputSummaryJson))
        {
            throw new ArgumentException("Die Decision-Log-Eingabezusammenfassung muss angegeben werden.", nameof(inputSummaryJson));
        }

        if (reasons.Count == 0)
        {
            throw new ArgumentException("Ein Decision-Log-Eintrag braucht mindestens eine Begruendung.", nameof(reasons));
        }

        Id = id;
        DecidedAtUtc = decidedAtUtc;
        ValidFromUtc = validFromUtc;
        ValidToUtc = validToUtc;
        Decision = decision;
        StateOfChargePercent = stateOfChargePercent;
        TibberPricePerKwh = tibberPricePerKwh;
        TibberPriceCurrency = tibberPriceCurrency;
        GridImportWatts = gridImportWatts;
        GridExportWatts = gridExportWatts;
        InputSummaryJson = inputSummaryJson;
        Reasons = reasons;
    }

    public Guid Id { get; }

    public DateTimeOffset DecidedAtUtc { get; }

    public DateTimeOffset ValidFromUtc { get; }

    public DateTimeOffset ValidToUtc { get; }

    public CurrentBatteryDecision Decision { get; }

    public decimal? StateOfChargePercent { get; }

    public decimal? TibberPricePerKwh { get; }

    public string? TibberPriceCurrency { get; }

    public int? GridImportWatts { get; }

    public int? GridExportWatts { get; }

    public string InputSummaryJson { get; }

    public IReadOnlyList<BatteryDecisionReason> Reasons { get; }
}
