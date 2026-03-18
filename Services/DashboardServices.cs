using Microsoft.Extensions.Options;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public record DashboardState(
    EnergyState CurrentState,
    IReadOnlyList<DecisionHistoryEntry> Decisions,
    IReadOnlyList<EnergyStateHistoryEntry> StateHistory,
    IReadOnlyList<TibberChartPoint> TibberPrices);

public interface IDashboardQueryService
{
    Task<DashboardState> GetAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TibberChartPoint>> GetChartPointsAsync(CancellationToken cancellationToken = default);
}

public class DashboardQueryService : IDashboardQueryService
{
    private readonly VictronMqttClient _victron;
    private readonly IDecisionHistoryStore _decisionHistoryStore;
    private readonly IEnergyStateHistoryStore _energyStateHistoryStore;
    private readonly TibberPriceProvider _tibberPriceProvider;
    private readonly DecisionEngine _decisionEngine;
    private readonly IConsumptionForecastService _consumptionForecastService;
    private readonly ControllerOptions _controllerOptions;

    public DashboardQueryService(
        VictronMqttClient victron,
        IDecisionHistoryStore decisionHistoryStore,
        IEnergyStateHistoryStore energyStateHistoryStore,
        TibberPriceProvider tibberPriceProvider,
        DecisionEngine decisionEngine,
        IConsumptionForecastService consumptionForecastService,
        IOptions<ControllerOptions> controllerOptions)
    {
        _victron = victron;
        _decisionHistoryStore = decisionHistoryStore;
        _energyStateHistoryStore = energyStateHistoryStore;
        _tibberPriceProvider = tibberPriceProvider;
        _decisionEngine = decisionEngine;
        _consumptionForecastService = consumptionForecastService;
        _controllerOptions = controllerOptions.Value;
    }

    public async Task<DashboardState> GetAsync(CancellationToken cancellationToken = default)
    {
        var current = _victron.GetCurrentState();
        var decisions = await _decisionHistoryStore.GetLast24HoursAsync(cancellationToken);
        var history = await _energyStateHistoryStore.GetLast24HoursAsync(cancellationToken);
        var tibberPrices = await GetChartPointsAsync(cancellationToken);

        return new DashboardState(current, decisions, history, tibberPrices);
    }

    public async Task<IReadOnlyList<TibberChartPoint>> GetChartPointsAsync(CancellationToken cancellationToken = default)
    {
        var prices = await _tibberPriceProvider.GetUpcomingPricesAsync(cancellationToken);
        var simulatedState = _victron.GetCurrentState();
        var result = new List<TibberChartPoint>();

        foreach (var price in prices.OrderBy(x => x.StartsAt))
        {
            var expectedConsumption = await _consumptionForecastService
                .GetExpectedConsumptionWhAsync(price.StartsAt, cancellationToken);

            var forecastState = simulatedState with
            {
                HouseConsumptionWatts = expectedConsumption
            };

            var decision = await _decisionEngine.BuildDecisionForPointAsync(
                forecastState,
                prices,
                price.StartsAt,
                cancellationToken);

            var nextState = ProjectStateForNextHour(forecastState, decision);

            result.Add(new TibberChartPoint(
                price.StartsAt,
                price.TotalPricePerKwh,
                decision.Action.ToString(),
                nextState.BatterySocPercent,
                forecastState.HouseConsumptionWatts,
                nextState.BatteryPowerWatts,
                nextState.PvPowerWatts,
                nextState.GridPowerWatts,
                decision.Reason));

            simulatedState = nextState;
        }

        return result;
    }

    private EnergyState ProjectStateForNextHour(EnergyState currentState, Decision decision)
    {
        var signedBatteryPowerWatts = decision.Action switch
        {
            BatteryAction.Charge => Math.Abs(decision.TargetPowerWatts),
            BatteryAction.Discharge => -Math.Abs(decision.TargetPowerWatts),
            _ => 0
        };

        var deltaKwh = signedBatteryPowerWatts / 1000.0;
        var deltaSoc = _controllerOptions.BatteryUsableCapacityKwh > 0
            ? (deltaKwh / _controllerOptions.BatteryUsableCapacityKwh) * 100.0
            : 0.0;

        var nextSoc = Math.Clamp(currentState.BatterySocPercent + deltaSoc, 0, 100);
        var pvPower = Math.Max(0, currentState.PvPowerWatts);
        var houseConsumption = Math.Max(0, currentState.HouseConsumptionWatts);

        var gridPower = houseConsumption - pvPower;
        if (decision.Action == BatteryAction.Charge)
        {
            gridPower += Math.Abs(decision.TargetPowerWatts);
        }
        else if (decision.Action == BatteryAction.Discharge)
        {
            gridPower -= Math.Abs(decision.TargetPowerWatts);
        }

        return currentState with
        {
            BatterySocPercent = nextSoc,
            BatteryPowerWatts = signedBatteryPowerWatts,
            GridPowerWatts = gridPower
        };
    }
}
