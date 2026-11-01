using System.Globalization;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public sealed record EvChargePlanView(EvChargePlan Plan, string SlotSource, EvTripPlanView Trips);

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
        var plan = EvChargePlanner.Plan(new EvChargePlanInput(
            now,
            car.StateOfChargePercent,
            car.TargetStateOfChargePercent,
            tripView.Car,
            await NumberAsync(ControllerSettingDefaults.EvCarBaseSocKey, cancellationToken),
            await NumberAsync(ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey, cancellationToken),
            slots,
            tripView.Plan.Trips));
        return new EvChargePlanView(plan, source, tripView);
    }

    private async Task<(IReadOnlyList<EvChargeSlotInput> Slots, string Source)> SlotsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (joint.Current.Plan is { } plan && now - plan.CalculatedAtUtc <= JointPlanMaximumAge && plan.Slots.Count > 0)
            return (plan.Slots.Select(slot => new EvChargeSlotInput(slot.TimeSlot.StartsAtUtc, slot.TimeSlot.EndsAtUtc, (double)slot.PricePerKwh,
                slot.IsPriceEstimated, Surplus(slot))).ToArray(), "Akkuplan");

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

    // PV that is left after house consumption and planned home battery charging would be fed in.
    private static double Surplus(JointBatterySlot slot)
    {
        var batteryChargeKwh = slot.Targets.Sum(target => Math.Max(0, target.SignedPowerWatts)) / 1000d * slot.TimeSlot.Duration.TotalHours;
        return Math.Max(0, (double)(slot.ExpectedPvKwh - slot.ExpectedConsumptionKwh) - batteryChargeKwh);
    }

    private async Task<double> NumberAsync(string key, CancellationToken cancellationToken) =>
        double.TryParse((await settings.GetSettingAsync(key, cancellationToken))?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"Die Einstellung {key} fehlt oder ist keine Zahl.");
}
