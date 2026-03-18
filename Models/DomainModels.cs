namespace TibberVictronController.Web.Models;

public record PricePoint(DateTimeOffset StartsAt, decimal TotalPricePerKwh);

public record EnergyState(
    double GridPowerWatts = 0,
    double BatterySocPercent = 0,
    double BatteryPowerWatts = 0,
    double HouseConsumptionWatts = 0,
    double PvPowerWatts = 0);

public record Decision(
    BatteryAction Action,
    double TargetPowerWatts,
    decimal CurrentPrice,
    string Reason);

public enum BatteryAction
{
    Hold,
    Charge,
    Discharge
}

public class DecisionHistoryEntry
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Action { get; set; } = string.Empty;
    public double TargetPowerWatts { get; set; }
    public decimal CurrentPrice { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class EnergyStateHistoryEntry
{
    public int Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public double GridPowerWatts { get; set; }
    public double BatterySocPercent { get; set; }
    public double BatteryPowerWatts { get; set; }
    public double HouseConsumptionWatts { get; set; }
    public double PvPowerWatts { get; set; }
}

public record TibberChartPoint(
    DateTimeOffset StartsAt,
    decimal Price,
    string Action,
    double ForecastSocPercent,
    double ForecastConsumptionWatts,
    double ForecastBatteryPowerWatts,
    double ForecastPvPowerWatts,
    double ForecastGridPowerWatts,
    string Reason);

public record DashboardResponse(
    EnergyState CurrentState,
    IReadOnlyList<DecisionHistoryEntry> Decisions,
    IReadOnlyList<EnergyStateHistoryEntry> StateHistory,
    IReadOnlyList<TibberChartPoint> TibberPrices);
