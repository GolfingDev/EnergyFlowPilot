using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>The existing Victron chart follows the same joint plan as hardware execution while joint control owns the site.</summary>
public sealed class JointAwareBatteryForecastService(IServiceProvider services) : IBatteryForecastService
{
    public Task<BatteryForecastResult> CalculateForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default) =>
        CalculateForecastAsync(new BatteryForecastRequest { StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc }, cancellationToken);

    public async Task<BatteryForecastResult> CalculateForecastAsync(BatteryForecastRequest request, CancellationToken cancellationToken = default)
    {
        if (!await JointBatteryOwnership.BlocksLegacyAsync(services.GetRequiredService<IControllerSettingStore>(), cancellationToken))
            return await services.GetRequiredService<BatteryForecastService>().CalculateForecastAsync(request, cancellationToken);
        var plan = services.GetRequiredService<JointBatteryRuntimeState>().Current.Plan;
        if (plan is null || services.GetRequiredService<IUtcClock>().UtcNow - plan.CalculatedAtUtc > TimeSpan.FromSeconds(75))
            throw new InvalidOperationException("Die gemeinsame Planung ist noch nicht verfuegbar oder veraltet.");
        var unit = plan.Batteries.Single(b => b.Id == "victron");
        var entries = plan.Slots.Where(s => s.TimeSlot.StartsAtUtc >= request.StartsAtUtc && s.TimeSlot.EndsAtUtc <= request.EndsAtUtc).Select(slot => {
            var target = slot.Targets.Single(t => t.Id == "victron");
            var decision = new CurrentBatteryDecision(new(target.SignedPowerWatts > 0 ? BatteryDecisionState.Charge : target.SignedPowerWatts < 0 ? BatteryDecisionState.Discharge : BatteryDecisionState.Idle,
                target.SignedPowerWatts > 0 ? slot.ChargeSource : null), Math.Abs(target.SignedPowerWatts));
            return new BatteryForecastEntry(slot.TimeSlot, slot.PricePerKwh, slot.Currency, slot.ExpectedPvKwh, slot.ExpectedConsumptionKwh,
                slot.ExpectedConsumptionKwh - slot.ExpectedPvKwh, target.SocBeforePercent, target.SocAfterPercent, decision,
                [new("JointBattery.Plan", slot.Reason)], slot.IsPriceEstimated);
        }).ToArray();
        return new(unit.State, unit.Configuration, entries);
    }
}
