using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public record DashboardState(
    EnergyState CurrentState,
    IReadOnlyList<DecisionHistoryEntry> Decisions,
    IReadOnlyList<EnergyStateHistoryEntry> StateHistory,
    IReadOnlyList<TibberChartPoint> TibberPrices,
    DateTime LastStateUpdateUtc,
    bool IsStateStale);

public interface IDashboardQueryService
{
    Task<DashboardState> GetAsync(CancellationToken cancellationToken = default);
}

public class DashboardQueryService : IDashboardQueryService
{
    private readonly VictronMqttClient _victron;
    private readonly IDecisionHistoryStore _decisionHistoryStore;
    private readonly IEnergyStateHistoryStore _energyStateHistoryStore;
    private readonly TibberPriceProvider _tibberPriceProvider;
    private readonly IPlanningEngine _planningEngine;

    public DashboardQueryService(
        VictronMqttClient victron,
        IDecisionHistoryStore decisionHistoryStore,
        IEnergyStateHistoryStore energyStateHistoryStore,
        TibberPriceProvider tibberPriceProvider,
        IPlanningEngine planningEngine)
    {
        _victron = victron;
        _decisionHistoryStore = decisionHistoryStore;
        _energyStateHistoryStore = energyStateHistoryStore;
        _tibberPriceProvider = tibberPriceProvider;
        _planningEngine = planningEngine;
    }

    public async Task<DashboardState> GetAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _victron.GetSnapshot();
        var decisions = await _decisionHistoryStore.GetLast24HoursAsync(cancellationToken);
        var history = await _energyStateHistoryStore.GetLast24HoursAsync(cancellationToken);
        var prices = await _tibberPriceProvider.GetUpcomingPricesAsync(cancellationToken);
        var plan = await _planningEngine.BuildPlanAsync(snapshot.State, prices, DateTimeOffset.Now, cancellationToken);

        var chart = plan.Select(x => new TibberChartPoint(
            x.StartsAt,
            x.Price,
            x.Action.ToString(),
            x.ForecastSocPercent,
            x.ForecastLoadWatts,
            x.ForecastPvWatts,
            x.Action switch
            {
                BatteryAction.Charge => Math.Abs(x.TargetPowerWatts),
                BatteryAction.Discharge => -Math.Abs(x.TargetPowerWatts),
                _ => 0
            },
            x.ForecastGridWatts,
            x.ReserveTargetPercent,
            x.Reason)).ToList();

        return new DashboardState(snapshot.State, decisions, history, chart, snapshot.LastMessageUtc, snapshot.IsStale);
    }
}
