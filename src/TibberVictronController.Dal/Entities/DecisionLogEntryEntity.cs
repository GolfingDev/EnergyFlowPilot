using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Entities;

public sealed class DecisionLogEntryEntity
{
    public Guid Id { get; set; }

    public DateTimeOffset DecidedAtUtc { get; set; }

    public DateTimeOffset ValidFromUtc { get; set; }

    public DateTimeOffset ValidToUtc { get; set; }

    public BatteryDecisionState DecisionState { get; set; }

    public BatteryChargeSource? ChargeSource { get; set; }

    public int TargetPowerWatts { get; set; }

    public decimal? StateOfChargePercent { get; set; }

    public decimal? TibberPricePerKwh { get; set; }

    public string? TibberPriceCurrency { get; set; }

    public int? GridImportWatts { get; set; }

    public int? GridExportWatts { get; set; }

    public string InputSummaryJson { get; set; } = string.Empty;

    public List<DecisionLogReasonEntity> Reasons { get; set; } = new();
}
