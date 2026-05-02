using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Groups direct decision dependencies so the service stays readable.
/// </summary>
public sealed class CurrentBatteryDecisionServiceDependencies
{
    public required IUtcClock UtcClock { get; init; }

    public required IBatteryStateProvider BatteryStateProvider { get; init; }

    public required IBatteryConfigurationProvider BatteryConfigurationProvider { get; init; }

    public required ICurrentSiteTelemetryProvider CurrentSiteTelemetryProvider { get; init; }

    public required ITibberPriceForecastProvider TibberPriceForecastProvider { get; init; }

    public required IControllerSettingStore ControllerSettingStore { get; init; }

    public required IDecisionLogRepository DecisionLogRepository { get; init; }
}
