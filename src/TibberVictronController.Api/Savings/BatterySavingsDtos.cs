namespace TibberVictronController.Api.Savings;

public sealed class BatterySavingsResponseDto
{
    public string Scope { get; init; } = "both";

    public IReadOnlyList<BatterySavingsBatteryDto> Batteries { get; init; } = [];

    public string CoverageNote { get; init; } = "Summe der erfassten Werte beider Speicher. Fehlende Messzeiten werden nicht hochgerechnet; historische E3/DC-Werte sind nur bei vorhandenen Messdaten und Preisen enthalten.";

    public string Period { get; init; } = string.Empty;

    public DateOnly? ReferenceDate { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    public string Currency { get; init; } = string.Empty;

    public BatterySavingsMetricsDto Aggregate { get; init; } = new();

    public IReadOnlyList<BatterySavingsDailySummaryDto> DailySummaries { get; init; } = Array.Empty<BatterySavingsDailySummaryDto>();
}

public class BatterySavingsMetricsDto
{
    public decimal GridChargedEnergyKwh { get; init; }

    public decimal GridChargeCost { get; init; }

    public decimal PvChargedEnergyKwh { get; init; }

    public decimal PvOpportunityCost { get; init; }

    public decimal DischargedEnergyKwh { get; init; }

    public decimal DischargeAvoidedCost { get; init; }

    public decimal NetSavings { get; init; }

    public decimal? AverageGridChargePricePerKwh { get; init; }

    public decimal? AveragePvOpportunityPricePerKwh { get; init; }

    public decimal? AverageDischargePricePerKwh { get; init; }
}

public sealed class BatterySavingsDailySummaryDto : BatterySavingsMetricsDto
{
    public string BatteryId { get; init; } = "victron";

    public DateOnly AccountingDate { get; init; }
}

public sealed class BatterySavingsErrorDto
{
    public string Message { get; init; } = string.Empty;
}

public sealed class BatterySavingsBatteryDto
{
    public string BatteryId { get; init; } = "";
    public string Label { get; init; } = "";
    public BatterySavingsMetricsDto? Aggregate { get; init; }
    public DateOnly? FirstRecordedDate { get; init; }
    public DateOnly? LastRecordedDate { get; init; }
    public decimal MeasuredHours { get; init; }
}
