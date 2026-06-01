namespace TibberVictronController.Api.Dashboard;

public sealed record DashboardTelemetryUpdateDto(
    int CurrentGridImportWatts,
    int? CurrentHouseConsumptionWatts,
    int? CurrentBatteryPowerWatts,
    decimal? StateOfChargePercent,
    DateTimeOffset MeasuredAtUtc);
