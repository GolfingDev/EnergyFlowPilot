using System.Net;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Prices;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

public sealed class EnergyForecastClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 21, 0, 0, TimeSpan.Zero);
    private const string Token = "secret-token-123";

    [Fact]
    public async Task ReadsExchangePricesWithoutTariffAndMarksForecasts()
    {
        var handler = new RecordingHttpMessageHandler("""
            {"generated_at":"2026-11-06T22:08:24+01:00","valid_until":"2026-11-09T00:00:00+01:00","plan":"pro","market_zone":"DE-LU","license_info":"CC BY 4.0",
             "data":[
              {"start":"2026-11-07T00:00:00+01:00","end":"2026-11-07T00:15:00+01:00","price_ct_kwh":22.5,"total_ct_kwh":22.5,"price_origin":"market"},
              {"start":"2026-11-08T00:00:00+01:00","end":"2026-11-08T00:15:00+01:00","price_ct_kwh":19.16,"total_ct_kwh":19.16,"price_origin":"forecast"}]}
            """);
        var client = new EnergyForecastClient(new HttpClient(handler), Settings(Token));

        var points = (await client.GetAsync())!;

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("fixed_cost_cent=0", query);
        Assert.Contains("vat=0", query);
        Assert.Contains("market_zone=DE-LU", query);
        Assert.Equal(2, points.Count);
        Assert.Equal(new PriceForecastPoint(new DateTimeOffset(2026, 11, 6, 23, 0, 0, TimeSpan.Zero), 0.225m, false), points[0]);
        Assert.True(points[1].IsForecast);
        Assert.Equal(0.1916m, points[1].SpotPricePerKwh);
    }

    [Fact]
    public async Task WithoutTokenNothingIsFetched()
    {
        var handler = new RecordingHttpMessageHandler("{}");

        Assert.Null(await new EnergyForecastClient(new HttpClient(handler), Settings(null)).GetAsync());
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RejectedTokenIsReportedWithoutTheToken()
    {
        var client = new EnergyForecastClient(new HttpClient(new RecordingHttpMessageHandler("{}", HttpStatusCode.Unauthorized)), Settings(Token));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync());

        Assert.Contains("401", exception.Message);
        Assert.DoesNotContain(Token, exception.Message);
    }

    private static FakeControllerSettingStore Settings(string? token) => new([
        new ControllerSetting(ControllerSettingDefaults.PriceForecastTokenKey, token, ControllerSettingSensitivity.Sensitive, Now),
        new ControllerSetting(ControllerSettingDefaults.EnergyChartsZoneKey, "DE-LU", ControllerSettingSensitivity.Normal, Now)
    ]);
}
