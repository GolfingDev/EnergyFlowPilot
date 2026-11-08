namespace TibberVictronController.Business.Energy;

public enum EnergySampleSource
{
    /// <summary>Decision log, about every 11 seconds since 2026-05-05: grid import/export and the valid retail price.</summary>
    DecisionLog,

    /// <summary>Live samples, every second since 2026-10-25: grid, both batteries, E3/DC PV and the wallbox.</summary>
    LiveSamples
}

/// <summary>
/// One measured point in time; its values hold until the next point, at most for the source's maximum gap. Signs:
/// grid positive = import; batteries positive = charging. Null means not measured at that moment.
/// </summary>
public sealed record EnergyPowerPoint(DateTimeOffset AtUtc)
{
    public double? GridWatts { get; init; }

    /// <summary>Victron battery (DC side, no loss correction).</summary>
    public double? PrimaryBatteryWatts { get; init; }

    /// <summary>E3/DC battery.</summary>
    public double? SupplementaryBatteryWatts { get; init; }

    /// <summary>E3/DC PV including the external inverter; all PV of the site.</summary>
    public double? SupplementaryPvWatts { get; init; }

    public double? EvWatts { get; init; }

    /// <summary>Retail price in EUR/kWh valid at that moment (all-in work price of the supplier).</summary>
    public decimal? PricePerKwh { get; init; }
}

/// <summary>
/// Measured energy of one quarter hour (UTC), stored permanently so evaluations survive the deletion of raw data. Each
/// group has its own coverage in seconds (0 to 900) and is null or zero only where it was not measured:
/// grid (import/export), balance (house consumption, PV, batteries; needs grid, both batteries and the E3/DC PV at the
/// same moment), wallbox and price.
/// </summary>
public sealed record EnergyQuarterHour(DateTimeOffset SlotStartUtc)
{
    public const int SlotSeconds = 900;

    public EnergySampleSource GridSource { get; init; }
    public int GridCoveredSeconds { get; init; }
    public double GridImportKwh { get; init; }
    public double GridExportKwh { get; init; }

    public int BalanceCoveredSeconds { get; init; }
    /// <summary>All AC loads of the site including heat pump and car: grid + PV - battery charging + battery discharging.</summary>
    public double? HouseConsumptionKwh { get; init; }
    public double? PvProductionKwh { get; init; }
    public double? PrimaryBatteryChargeKwh { get; init; }
    public double? PrimaryBatteryDischargeKwh { get; init; }
    public double? SupplementaryBatteryChargeKwh { get; init; }
    public double? SupplementaryBatteryDischargeKwh { get; init; }

    public int EvCoveredSeconds { get; init; }
    public double? EvChargingKwh { get; init; }

    public int PriceCoveredSeconds { get; init; }
    public decimal? PricePerKwh { get; init; }

    public DateTimeOffset ComputedAtUtc { get; init; }

    /// <summary>Import cost of the measured grid energy; null without price. Negative prices give negative cost.</summary>
    public decimal? ImportCost => PricePerKwh is { } price ? price * (decimal)GridImportKwh : null;
}
