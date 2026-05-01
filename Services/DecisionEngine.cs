using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class DecisionEngine
{
    private readonly IPlanningEngine _planningEngine;
    private readonly PlanningOptions _planningOptions;

    public DecisionEngine(IPlanningEngine planningEngine, Microsoft.Extensions.Options.IOptions<PlanningOptions> planningOptions)
    {
        _planningEngine = planningEngine;
        _planningOptions = planningOptions.Value;
    }

    public async Task<Decision> BuildDecisionAsync(
        EnergyState state,
        IReadOnlyList<PricePoint> prices,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var plan = await _planningEngine.BuildPlanAsync(state, prices, now, cancellationToken);
        var slotStart = FloorToResolution(now, _planningOptions.SlotResolutionMinutes);
        var current = plan.FirstOrDefault(x => x.StartsAt == slotStart)
            ?? plan.OrderBy(x => Math.Abs((x.StartsAt - now).TotalMinutes)).FirstOrDefault();

        if (current is null)
        {
            return new Decision(BatteryAction.Hold, 0, 0, "Kein Plan verfügbar");
        }

        return new Decision(current.Action, current.TargetPowerWatts, current.Price, current.Reason);
    }

    private static DateTimeOffset FloorToResolution(DateTimeOffset value, int resolutionMinutes)
    {
        var flooredMinute = (value.Minute / resolutionMinutes) * resolutionMinutes;
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, flooredMinute, 0, value.Offset);
    }
}
