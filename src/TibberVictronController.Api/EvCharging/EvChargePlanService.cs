using System.Globalization;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public sealed record EvChargePlanView(EvChargePlan Plan, string SlotSource, EvTripPlanView Trips)
{
    public EvBaseChargeChoiceState? BaseChoice { get; init; }
}

/// <summary>
/// Collects the inputs of the car charging plan. Prices and PV surplus come from the joint battery plan: its slots
/// already hold price, PV and consumption forecasts and the planned home battery charging, so the car only gets PV
/// that would otherwise be fed in. Without a current joint plan the plain price forecast is used (no PV surplus).
/// </summary>
public sealed class EvChargePlanService(
    EvTripPlanningService trips,
    JointBatteryRuntimeState joint,
    IServiceProvider services,
    IControllerSettingStore settings,
    EvChargeLoadForecast carLoad,
    IUtcClock clock)
{
    private static readonly TimeSpan JointPlanMaximumAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PriceHorizon = TimeSpan.FromHours(36);

    public async Task<EvChargePlanView> GetAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var tripView = await trips.GetPlanAsync(cancellationToken);
        var (slots, source) = await SlotsAsync(now, cancellationToken);
        var car = services.GetRequiredService<ICarStatusSource>().Car;
        var choice = await services.GetRequiredService<EvBaseChargeChoiceService>().GetAsync(cancellationToken);
        var plan = EvChargePlanner.Plan(new EvChargePlanInput(
            now,
            car.StateOfChargeEstimatePercent(tripView.Car.UsableCapacityKwh),
            car.TargetStateOfChargePercent,
            tripView.Car,
            await NumberAsync(ControllerSettingDefaults.EvCarBaseSocKey, cancellationToken),
            await NumberAsync(ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey, cancellationToken),
            slots,
            tripView.Plan.Trips) { BaseMode = choice.Mode, ImmediateTargetSocPercent = choice.ImmediateTargetPercent });
        return new EvChargePlanView(plan, source, tripView) { BaseChoice = choice };
    }

    private async Task<(IReadOnlyList<EvChargeSlotInput> Slots, string Source)> SlotsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (joint.Current.Plan is { } plan && now - plan.CalculatedAtUtc <= JointPlanMaximumAge && plan.Slots.Count > 0)
            return (plan.Slots.Select(slot => ToCarSlot(slot, carLoad.KwhFor(slot.TimeSlot.StartsAtUtc))).ToArray(), "Akkuplan");

        var start = JointPlanInputFallback.QuarterStart(now);
        IReadOnlyList<TibberPriceForecastSlot> prices;
        try
        {
            prices = await services.GetRequiredService<IElectricityPriceForecastProvider>().GetPriceForecastAsync(start, start + PriceHorizon, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            prices = services.GetService<JointBatteryInputCache>()?.Prices.Items ?? [];
        }

        return (prices.Select(price => new EvChargeSlotInput(price.TimeSlot.StartsAtUtc, price.TimeSlot.EndsAtUtc, (double)price.TotalPricePerKwh,
            price.IsEstimated, 0)).ToArray(), "Preisprognose");
    }

    /// <summary>
    /// Splits the PV of a joint plan slot for the car: what would be fed in (free surplus) and what the home batteries
    /// would store, offered at the batteries' marginal value so PV goes where it saves more (operator 2026-11-01). The
    /// joint plan's consumption already contains the car's own planned charging; it is added back so the car does not
    /// plan against itself and the two plans do not oscillate.
    /// </summary>
    public static EvChargeSlotInput ToCarSlot(JointBatterySlot slot, double plannedCarKwh)
    {
        var hours = slot.TimeSlot.Duration.TotalHours;
        var pvAfterHouse = Math.Max(0, (double)(slot.ExpectedPvKwh - slot.ExpectedConsumptionKwh) + plannedCarKwh);
        var charging = slot.Targets.Where(target => target.SignedPowerWatts > 0).ToArray();
        var batteryChargeKwh = charging.Sum(target => target.SignedPowerWatts) / 1000d * hours;
        var batteryPv = Math.Min(batteryChargeKwh, pvAfterHouse);
        // Taking one kWh away from a charging battery loses about 0.95 kWh stored (charging losses).
        var batteryValue = charging.Length == 0 ? 0 : (double)charging.Min(target => target.EnergyValuePerKwh) * 0.95;
        return new EvChargeSlotInput(slot.TimeSlot.StartsAtUtc, slot.TimeSlot.EndsAtUtc, (double)slot.PricePerKwh, slot.IsPriceEstimated,
            Math.Max(0, pvAfterHouse - batteryChargeKwh))
        {
            BatteryPvKwh = batteryPv,
            BatteryPvValuePerKwh = batteryValue
        };
    }

    private async Task<double> NumberAsync(string key, CancellationToken cancellationToken) =>
        double.TryParse((await settings.GetSettingAsync(key, cancellationToken))?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"Die Einstellung {key} fehlt oder ist keine Zahl.");
}
