using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryGridExportAbsorptionPolicyTests
{
    private static readonly DateTimeOffset StartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CalculateTargetPowerAbsorbsGridExportWhenBatteryCanCharge()
    {
        var policy = new BatteryGridExportAbsorptionPolicy();
        var batteryState = new BatteryState(50m, StartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var targetPowerWatts = policy.CalculateTargetPowerWatts(
            currentGridImportWatts: -1200,
            batteryState,
            batteryConfiguration,
            priceForecast: Array.Empty<TibberPriceForecastSlot>(),
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(1200, targetPowerWatts);
    }

    [Fact]
    public void CalculateTargetPowerUsesMaximumChargePowerWhenGridExportIsHigher()
    {
        var policy = new BatteryGridExportAbsorptionPolicy();
        var batteryState = new BatteryState(50m, StartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var targetPowerWatts = policy.CalculateTargetPowerWatts(
            currentGridImportWatts: -4200,
            batteryState,
            batteryConfiguration,
            priceForecast: Array.Empty<TibberPriceForecastSlot>(),
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(3000, targetPowerWatts);
    }

    [Fact]
    public void CalculateTargetPowerReturnsZeroWhenBatteryIsFull()
    {
        var policy = new BatteryGridExportAbsorptionPolicy();
        var batteryState = new BatteryState(100m, StartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var targetPowerWatts = policy.CalculateTargetPowerWatts(
            currentGridImportWatts: -1200,
            batteryState,
            batteryConfiguration,
            priceForecast: Array.Empty<TibberPriceForecastSlot>(),
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(0, targetPowerWatts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void CalculateTargetPowerReturnsZeroWhenThereIsNoGridExport(int currentGridImportWatts)
    {
        var policy = new BatteryGridExportAbsorptionPolicy();
        var batteryState = new BatteryState(50m, StartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var targetPowerWatts = policy.CalculateTargetPowerWatts(
            currentGridImportWatts,
            batteryState,
            batteryConfiguration,
            priceForecast: Array.Empty<TibberPriceForecastSlot>(),
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(0, targetPowerWatts);
    }

    [Fact]
    public void CalculateTargetPowerCanPreserveHeadroomWhenFutureNegativePriceIsMoreValuableThanFeedInCompensation()
    {
        var policy = new BatteryGridExportAbsorptionPolicy();
        var batteryState = new BatteryState(80m, StartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);
        var priceForecast = new[]
        {
            CreatePriceSlot(StartsAtUtc.AddHours(1), -0.20m)
        };

        var targetPowerWatts = policy.CalculateTargetPowerWatts(
            currentGridImportWatts: -1200,
            batteryState,
            batteryConfiguration,
            priceForecast,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(0, targetPowerWatts);
    }

    [Fact]
    public void CalculateTargetPowerAbsorbsGridExportWhenFeedInCompensationIsAtLeastAsValuableAsFutureNegativePrice()
    {
        var policy = new BatteryGridExportAbsorptionPolicy();
        var batteryState = new BatteryState(80m, StartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);
        var priceForecast = new[]
        {
            CreatePriceSlot(StartsAtUtc.AddHours(1), -0.05m)
        };

        var targetPowerWatts = policy.CalculateTargetPowerWatts(
            currentGridImportWatts: -1200,
            batteryState,
            batteryConfiguration,
            priceForecast,
            feedInCompensationPricePerKwh: 0.08m);

        Assert.Equal(1200, targetPowerWatts);
    }

    private static TibberPriceForecastSlot CreatePriceSlot(DateTimeOffset startsAtUtc, decimal totalPricePerKwh)
    {
        return new TibberPriceForecastSlot(
            new ForecastTimeSlot(startsAtUtc, startsAtUtc.AddMinutes(15)),
            totalPricePerKwh,
            "EUR");
    }
}
