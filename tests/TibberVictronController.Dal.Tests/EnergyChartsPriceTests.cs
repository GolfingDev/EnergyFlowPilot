using System.Net;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Prices;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

public sealed class EnergyChartsPriceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Start; }
    private static string Json(int stepMinutes, params decimal?[] prices) => JsonSerializer.Serialize(new {
        unix_seconds = prices.Select((_, i) => Start.AddMinutes(i * stepMinutes).ToUnixTimeSeconds()), price = prices, unit = "EUR / MWh" });
    private static FakeControllerSettingStore Settings(string? surcharge = ".15") => new([
        new(ControllerSettingDefaults.EnergyChartsSurchargeKey, surcharge, ControllerSettingSensitivity.Normal, Start),
        new(ControllerSettingDefaults.EnergyChartsVatKey, "19", ControllerSettingSensitivity.Normal, Start),
        new(ControllerSettingDefaults.EnergyChartsZoneKey, "DE-LU", ControllerSettingSensitivity.Normal, Start)]);

    [Fact]
    public async Task ConvertsMwhNetPricesToGrossTariffWithoutTaxingGrossSurchargeAgain()
    {
        var handler = new RecordingHttpMessageHandler(Json(15, 100, -200, 50));
        var provider = new EnergyChartsPriceForecastProvider(new(handler), Settings(), new Clock(), new());
        var prices = await provider.GetPriceForecastAsync(Start.AddMinutes(5), Start.AddMinutes(45));
        Assert.Equal(3, prices.Count); // The current, already-started quarter must remain available.
        Assert.Equal(Start, prices[0].TimeSlot.StartsAtUtc);
        Assert.Equal(.269m, prices[0].TotalPricePerKwh);
        Assert.Equal(-.088m, prices[1].TotalPricePerKwh);
        Assert.All(prices, p => { Assert.Equal("EUR", p.Currency); Assert.False(p.IsEstimated); });
        Assert.Contains("bzn=DE-LU", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task HourlyDataSplitsIntoPublishedQuartersAcrossRepeatedAutumnHour()
    {
        var provider = new EnergyChartsPriceForecastProvider(new(new RecordingHttpMessageHandler(Json(60, 100, 200, 300))), Settings(), new Clock(), new());
        var result = await provider.GetPriceForecastAsync(Start, Start.AddHours(3));
        Assert.Equal(12, result.Count);
        Assert.Equal(12, result.Select(p => p.TimeSlot.StartsAtUtc).Distinct().Count());
        Assert.All(result.Take(4), p => Assert.Equal(.269m, p.TotalPricePerKwh));
        Assert.All(result.Skip(4).Take(4), p => Assert.Equal(.388m, p.TotalPricePerKwh));
    }

    [Fact]
    public async Task RawCacheReappliesChangedTariffAndNeverInventsNullPrices()
    {
        var handler = new RecordingHttpMessageHandler(Json(15, 100, null, 200)); var settings = Settings();
        var provider = new EnergyChartsPriceForecastProvider(new(handler), settings, new Clock(), new());
        var old = await provider.GetPriceForecastAsync(Start, Start.AddMinutes(45));
        await settings.SaveSettingAsync(new(ControllerSettingDefaults.EnergyChartsSurchargeKey, ".20", ControllerSettingSensitivity.Normal, Start));
        var updated = await provider.GetPriceForecastAsync(Start, Start.AddMinutes(45));
        Assert.Equal(2, updated.Count); Assert.Equal(1, handler.RequestCount);
        Assert.Equal(.05m, updated[0].TotalPricePerKwh - old[0].TotalPricePerKwh);
    }

    [Theory]
    [InlineData("{\"unix_seconds\":[1792886400,1792887300],\"price\":[100],\"unit\":\"EUR/MWh\"}")]
    [InlineData("{\"unix_seconds\":[1792886400,1792886400],\"price\":[100,200],\"unit\":\"EUR/MWh\"}")]
    [InlineData("{\"unix_seconds\":[1792886400,1792887300],\"price\":[100,200],\"unit\":\"GBP/MWh\"}")]
    public async Task RejectsMalformedOrWrongCurrencySeries(string json)
    {
        var provider = new EnergyChartsPriceForecastProvider(new(new RecordingHttpMessageHandler(json)), Settings(), new Clock(), new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
    }

    [Fact]
    public async Task MissingMarkupAndHttpFailureDoNotBecomeFreeElectricityOrSupplierFallback()
    {
        var handler = new RecordingHttpMessageHandler("{}", HttpStatusCode.ServiceUnavailable);
        var provider = new EnergyChartsPriceForecastProvider(new(handler), Settings(null), new Clock(), new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Equal(0, handler.RequestCount);
        var settings = Settings(); provider = new(new(handler), settings, new Clock(), new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Equal(1, handler.RequestCount);
    }
}
