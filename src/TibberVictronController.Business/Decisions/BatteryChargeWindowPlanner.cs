using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Selects the cheapest forecast slots required to charge the battery.
/// </summary>
public sealed class BatteryChargeWindowPlanner
{
    /// <summary>
    /// Plans the cheapest 15-minute slots that can fill the battery based on capacity and maximum charge power.
    /// </summary>
    public BatteryChargeWindowPlan PlanCheapestSlotsForFullCharge(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration)
    {
        if (priceForecast is null)
        {
            throw new ArgumentNullException(nameof(priceForecast), "Die Tibber-Preisprognose darf nicht null sein.");
        }

        if (batteryState is null)
        {
            throw new ArgumentNullException(nameof(batteryState), "Der Akkuladestand darf nicht null sein.");
        }

        if (batteryConfiguration is null)
        {
            throw new ArgumentNullException(nameof(batteryConfiguration), "Die Batteriekonfiguration darf nicht null sein.");
        }

        var requiredEnergyKwh = CalculateRequiredEnergyToFullKwh(batteryState, batteryConfiguration);
        var maximumChargePowerKw = batteryConfiguration.MaximumChargePowerWatts / 1000m;
        var requiredChargeDuration = TimeSpan.FromHours((double)(requiredEnergyKwh / maximumChargePowerKw));
        var plannedChargeSlots = SelectCheapestChargeSlots(priceForecast, requiredEnergyKwh, maximumChargePowerKw);

        return new BatteryChargeWindowPlan(
            requiredEnergyKwh,
            requiredChargeDuration,
            plannedChargeSlots);
    }

    private static IReadOnlyList<ForecastTimeSlot> SelectCheapestChargeSlots(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal requiredEnergyKwh,
        decimal maximumChargePowerKw)
    {
        if (requiredEnergyKwh <= 0m)
        {
            return Array.Empty<ForecastTimeSlot>();
        }

        var selectedSlots = new List<TibberPriceForecastSlot>();
        var plannedEnergyKwh = 0m;

        // The Decision Engine chooses the cheapest individual 15-minute slots first because the best prices may not be contiguous.
        foreach (var priceSlot in priceForecast.OrderBy(priceSlot => priceSlot.TotalPricePerKwh).ThenBy(priceSlot => priceSlot.TimeSlot.StartsAtUtc))
        {
            selectedSlots.Add(priceSlot);
            plannedEnergyKwh += maximumChargePowerKw * (decimal)priceSlot.TimeSlot.Duration.TotalHours;

            if (plannedEnergyKwh >= requiredEnergyKwh)
            {
                break;
            }
        }

        return selectedSlots
            .OrderBy(priceSlot => priceSlot.TimeSlot.StartsAtUtc)
            .Select(priceSlot => priceSlot.TimeSlot)
            .ToArray();
    }

    private static decimal CalculateRequiredEnergyToFullKwh(
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration)
    {
        var missingStateOfChargePercent = 100m - batteryState.StateOfChargePercent;

        return batteryConfiguration.TotalCapacityKwh * missingStateOfChargePercent / 100m;
    }
}
