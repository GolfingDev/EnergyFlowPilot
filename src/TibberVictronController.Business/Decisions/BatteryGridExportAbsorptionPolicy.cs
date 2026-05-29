using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Calculates how much current grid export should be absorbed by battery charging.
/// </summary>
public sealed class BatteryGridExportAbsorptionPolicy
{
    /// <summary>
    /// Calculates a charge setpoint for negative grid import while preserving headroom for more valuable negative-price charging.
    /// </summary>
    public int CalculateTargetPowerWatts(
        int currentGridImportWatts,
        BatteryState batteryState,
        BatteryConfiguration batteryConfiguration,
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh)
    {
        if (batteryState is null)
        {
            throw new ArgumentNullException(nameof(batteryState), "Der Akkuladestand darf nicht null sein.");
        }

        if (batteryConfiguration is null)
        {
            throw new ArgumentNullException(nameof(batteryConfiguration), "Die Batteriekonfiguration darf nicht null sein.");
        }

        if (priceForecast is null)
        {
            throw new ArgumentNullException(nameof(priceForecast), "Die Tibber-Preisprognose darf nicht null sein.");
        }

        if (feedInCompensationPricePerKwh < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(feedInCompensationPricePerKwh), "Die Einspeiseverguetung darf nicht negativ sein.");
        }

        if (currentGridImportWatts >= 0 || batteryState.StateOfChargePercent >= batteryConfiguration.PlanningMaximumStateOfChargePercent)
        {
            return 0;
        }

        if (ShouldPreserveHeadroomForFutureNegativePriceCharging(priceForecast, feedInCompensationPricePerKwh))
        {
            return 0;
        }

        var currentGridExportWatts = Math.Abs(currentGridImportWatts);

        return Math.Min(currentGridExportWatts, batteryConfiguration.MaximumChargePowerWatts);
    }

    private static bool ShouldPreserveHeadroomForFutureNegativePriceCharging(
        IReadOnlyList<TibberPriceForecastSlot> priceForecast,
        decimal feedInCompensationPricePerKwh)
    {
        var mostValuableNegativePrice = priceForecast
            .Where(priceSlot => priceSlot.TotalPricePerKwh < 0m)
            .Select(priceSlot => Math.Abs(priceSlot.TotalPricePerKwh))
            .DefaultIfEmpty(0m)
            .Max();

        // Exporting PV can be rational when its compensation plus later negative-price charging beats charging the battery immediately.
        return mostValuableNegativePrice > feedInCompensationPricePerKwh;
    }
}
