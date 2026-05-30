namespace TibberVictronController.Api.Dashboard;

public sealed record DashboardTelemetryUpdateDto(
    int CurrentGridImportWatts,
    int CurrentPvProductionWatts,
    int? CurrentBatteryPowerWatts,
    decimal? StateOfChargePercent,
    DateTimeOffset MeasuredAtUtc);
