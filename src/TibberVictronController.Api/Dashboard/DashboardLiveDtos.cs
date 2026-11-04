namespace TibberVictronController.Api.Dashboard;

public sealed record DashboardTelemetryUpdateDto(
    int CurrentGridImportWatts,
    int? CurrentHouseConsumptionWatts,
    int? CurrentBatteryPowerWatts,
    decimal? StateOfChargePercent,
    DateTimeOffset MeasuredAtUtc);

/// <summary>Area whose data changed: "joint", "wallbox" or "ev".</summary>
public sealed record DashboardChangedDto(string Area);
