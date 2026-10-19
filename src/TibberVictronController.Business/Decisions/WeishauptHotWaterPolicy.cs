using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

public static class WeishauptHotWaterPolicy
{
    public static bool ShouldBoost(WeishauptHotWaterSettings settings, DateTimeOffset nowUtc,
        bool alreadyRaised, TibberPriceForecastSlot? price, CurrentSiteTelemetry? telemetry,
        WeishauptHotWaterPricePlan? plan = null)
    {
        var cheap = price is { IsEstimated: false, Currency: "EUR" } &&
            price.TimeSlot.StartsAtUtc <= nowUtc && nowUtc < price.TimeSlot.EndsAtUtc &&
            plan?.ReferencePricePerKwh is { } reference && plan.StartsAtUtc <= nowUtc && nowUtc < plan.EndsAtUtc &&
            reference - WeishauptHotWaterPricePlanner.EffectivePrice(price.TotalPricePerKwh, settings.ExtraEnergyPercent)
                >= settings.MinimumPriceAdvantagePerKwh - (alreadyRaised ? 0.02m : 0m) &&
            (alreadyRaised || plan.EstimatedAdvantagePerKwh >= settings.MinimumPriceAdvantagePerKwh);

        // Net export caused by batteries is not renewable surplus available to the heat pump.
        var fresh = telemetry is not null && telemetry.MeasuredAtUtc <= nowUtc &&
            nowUtc - telemetry.MeasuredAtUtc <= TimeSpan.FromSeconds(120);
        var surplus = telemetry is null ? 0L : -(long)telemetry.CurrentGridImportWatts -
            Math.Max(0L, -(long)(telemetry.CurrentBatteryPowerWatts ?? 0)) -
            Math.Max(0, telemetry.SupplementaryBatteryDischargeWatts);
        var pv = fresh && telemetry!.CurrentPvProductionWatts > 0 &&
            telemetry.CurrentBatteryPowerWatts.HasValue &&
            surplus >= (alreadyRaised ? Math.Min(200, settings.PvSurplusWatts) : settings.PvSurplusWatts);
        return settings.IsEnabled && (cheap || pv);
    }
}
