namespace TibberVictronController.Dal.Entities;

/// <summary>
/// Measured consumption and heating degrees of one local day, kept beyond the raw sample retention.
/// </summary>
public sealed class ConsumptionDailySummaryEntity
{
    /// <summary>
    /// Gets or sets the local date as yyyy-MM-dd.
    /// </summary>
    public string LocalDate { get; set; } = string.Empty;

    public decimal EnergyKwh { get; set; }

    public int SlotCount { get; set; }

    public decimal HeatingDegrees { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
