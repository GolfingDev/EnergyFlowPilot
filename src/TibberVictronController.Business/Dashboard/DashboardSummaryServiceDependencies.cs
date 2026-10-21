using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Business.Dashboard;

/// <summary>
/// Groups the inputs of the dashboard summary so the service constructor stays small.
/// </summary>
public sealed class DashboardSummaryServiceDependencies
{
    public required IUtcClock UtcClock { get; init; }

    public required IDecisionLogRepository DecisionLogRepository { get; init; }

    public required ILiveConsumptionRepository LiveConsumptionRepository { get; init; }

    public required IBatteryForecastService BatteryForecastService { get; init; }

    public required IBatteryConfigurationProvider BatteryConfigurationProvider { get; init; }

    public required IElectricityPriceForecastProvider TibberPriceForecastProvider { get; init; }

    public required IControllerSettingStore ControllerSettingStore { get; init; }
}
