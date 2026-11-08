namespace TibberVictronController.Dal.Entities;

public sealed class EnergyQuarterHourEntity
{
    public DateTimeOffset SlotStartUtc { get; set; }
    public string GridSource { get; set; } = string.Empty;
    public int GridCoveredSeconds { get; set; }
    public double GridImportKwh { get; set; }
    public double GridExportKwh { get; set; }
    public int BalanceCoveredSeconds { get; set; }
    public double? HouseConsumptionKwh { get; set; }
    public double? PvProductionKwh { get; set; }
    public double? PrimaryBatteryChargeKwh { get; set; }
    public double? PrimaryBatteryDischargeKwh { get; set; }
    public double? SupplementaryBatteryChargeKwh { get; set; }
    public double? SupplementaryBatteryDischargeKwh { get; set; }
    public int EvCoveredSeconds { get; set; }
    public double? EvChargingKwh { get; set; }
    public int PriceCoveredSeconds { get; set; }
    public decimal? PricePerKwh { get; set; }
    public DateTimeOffset ComputedAtUtc { get; set; }
}
