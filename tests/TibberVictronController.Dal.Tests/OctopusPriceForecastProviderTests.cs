using System.Net;
using System.Text.Json;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Octopus;
using TibberVictronController.Dal.Prices;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Tibber;

namespace TibberVictronController.Dal.Tests;

public sealed class OctopusPriceForecastProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
    private const string Token = """{"data":{"obtainKrakenToken":{"token":"test-jwt"}}}""";
    private const string Entry = """
        {"validFrom":"2026-10-21T10:00:00+02:00","validTo":"2026-10-21T10:15:00+02:00",
         "unitRateInformation":{"rates":[{"latestGrossUnitRateCentsPerKwh":"-2.5"}]}}
        """;
    private static string Wrap(string entries) =>
        """{"data":{"account":{"properties":[{"electricityMalos":[{"agreements":[{"unitRateForecast":[""" +
        entries + """]}]}]}]}}}""";

    [Fact]
    public async Task UsesGermanEndpointApiKeyTokenAndGrossCentsIncludingNegativePrices()
    {
        var fixture = new Fixture(Wrap(Entry));
        var prices = await fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1));
        var slot = Assert.Single(prices);
        Assert.Equal(-0.025m, slot.TotalPricePerKwh);
        Assert.Equal("EUR", slot.Currency);
        Assert.Equal(Start, slot.TimeSlot.StartsAtUtc);
        Assert.False(slot.IsEstimated);
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        using var authentication = JsonDocument.Parse(fixture.Handler.Bodies[0]);
        Assert.Equal("test-api-key", authentication.RootElement.GetProperty("variables").GetProperty("input").GetProperty("APIKey").GetString());
        Assert.False(authentication.RootElement.GetProperty("variables").GetProperty("input").TryGetProperty("apiKey", out _));
        Assert.Null(fixture.Handler.Headers[0]);
        Assert.Equal("test-jwt", fixture.Handler.Headers[1]);
        Assert.DoesNotContain("test-api-key", fixture.Handler.Bodies[1]);
        using var query = JsonDocument.Parse(fixture.Handler.Bodies[1]);
        Assert.Equal("A-TEST", query.RootElement.GetProperty("variables").GetProperty("accountNumber").GetString());
        Assert.Contains("latestGrossUnitRateCentsPerKwh", fixture.Handler.Bodies[1]);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("32.17", 0.3217)]
    public async Task AcceptsNumericAndStringDecimalPrices(string value, double expected)
    {
        var fixture = new Fixture(Wrap(Entry.Replace("\"-2.5\"", value)));
        var slots = await fixture.Provider.GetPriceForecastAsync(Start, Start.AddMinutes(15));
        Assert.Equal((decimal)expected, Assert.Single(slots).TotalPricePerKwh);
    }

    [Fact]
    public async Task DaylightSavingOffsetsRemainDistinctUtcSlots()
    {
        var entry = Entry.Replace("2026-10-21T10:00:00+02:00", "2026-10-25T02:00:00+02:00")
            .Replace("2026-10-21T10:15:00+02:00", "2026-10-25T02:15:00+02:00");
        var fixture = new Fixture(Wrap(entry + "," + entry.Replace("+02:00", "+01:00")));
        var start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var prices = await fixture.Provider.GetPriceForecastAsync(start, start.AddHours(2));
        Assert.Equal(2, prices.Count);
        Assert.Equal(TimeSpan.FromHours(1), prices[1].TimeSlot.StartsAtUtc - prices[0].TimeSlot.StartsAtUtc);
    }

    [Fact]
    public async Task CacheIsSharedButRangeFilteringAndCredentialRotationRemainCorrect()
    {
        var fixture = new Fixture(Wrap(Entry));
        await fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1));
        Assert.Empty(await fixture.Provider.GetPriceForecastAsync(Start.AddDays(1), Start.AddDays(2)));
        Assert.Equal(2, fixture.Handler.Bodies.Count);
        fixture.Clock.UtcNow = Start.AddMinutes(11);
        await fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1));
        Assert.Equal(3, fixture.Handler.Bodies.Count); // price refresh, same token
        fixture.Clock.UtcNow = Start.AddMinutes(46);
        await fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1));
        Assert.Equal(5, fixture.Handler.Bodies.Count); // proactive token renewal
        await fixture.Settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.OctopusApiKeyKey,
            "rotated-key", ControllerSettingSensitivity.Sensitive, fixture.Clock.UtcNow));
        await fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1));
        Assert.Equal(7, fixture.Handler.Bodies.Count);
        Assert.Contains("rotated-key", fixture.Handler.Bodies[5]);
    }

    [Theory]
    [InlineData("hourly")]
    [InlineData("duplicate")]
    [InlineData("multipleMalos")]
    [InlineData("multipleRates")]
    [InlineData("missingPrice")]
    [InlineData("empty")]
    [InlineData("graphql")]
    [InlineData("malformed")]
    public async Task RejectsAmbiguousOrInvalidDataWithoutLeakingErrorsAndThrottlesRetries(string kind)
    {
        var response = kind switch
        {
            "hourly" => Wrap(Entry.Replace("10:15:00", "11:00:00")),
            "duplicate" => Wrap(Entry + "," + Entry),
            "multipleMalos" => Wrap(Entry).Replace("\"electricityMalos\":[", "\"electricityMalos\":[{\"agreements\":[{\"unitRateForecast\":[" + Entry + "]}]},"),
            "multipleRates" => Wrap(Entry.Replace("\"rates\":[", "\"rates\":[{\"latestGrossUnitRateCentsPerKwh\":3},")),
            "missingPrice" => Wrap(Entry.Replace("latestGrossUnitRateCentsPerKwh", "differentField")),
            "empty" => Wrap(""),
            "graphql" => """{"errors":[{"message":"test-api-key test-jwt A-TEST"}],"data":null}""",
            _ => "not json test-api-key"
        };
        var fixture = new Fixture(response);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.DoesNotContain("test-api-key", error.ToString());
        Assert.DoesNotContain("test-jwt", error.ToString());
        Assert.DoesNotContain("A-TEST", error.ToString());
        Assert.Contains("Octopus", error.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Equal(2, fixture.Handler.Bodies.Count);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailuresDoNotUseOldPricesOrFallback(int status)
    {
        var fixture = new Fixture(Wrap(Entry));
        await fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1));
        fixture.Clock.UtcNow = Start.AddMinutes(11);
        fixture.Handler.Status = (HttpStatusCode)status;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
    }

    [Fact]
    public async Task SelectedProviderRoutesAllCallsToOctopusAndUnknownSelectionFailsClosed()
    {
        var fixture = new Fixture(Wrap(Entry));
        await fixture.Settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.ElectricityPriceProviderKey,
            "octopusGermany", ControllerSettingSensitivity.Normal, Start));
        var selected = fixture.Selector();
        Assert.Single(await selected.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Equal("octopusGermany", selected.Source);
        // A new request scope observes a setting change; no cross-supplier cached fallback.
        await fixture.Settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.ElectricityPriceProviderKey,
            "invalid", ControllerSettingSensitivity.Normal, Start));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Selector().GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Equal(2, fixture.Handler.Bodies.Count);
    }

    [Fact]
    public async Task DefaultRemainsTibberAndOctopusWithoutCredentialsNeverCallsNetwork()
    {
        var fixture = new Fixture(Wrap(Entry));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Selector().GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Contains("Tibber", error.Message); // no Tibber token configured, and no Octopus fallback
        await fixture.Settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.OctopusApiKeyKey,
            null, ControllerSettingSensitivity.Sensitive, Start));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-configured")]
    public async Task UnconfiguredAccountIsRejectedBeforeAuthentication(string? account)
    {
        var fixture = new Fixture(Wrap(Entry));
        await fixture.Settings.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.OctopusAccountNumberKey,
            account, ControllerSettingSensitivity.Normal, Start));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1)));
        Assert.Contains("Kundennummer", error.Message);
        Assert.Empty(fixture.Handler.Bodies);
    }

    [Fact]
    public async Task CancellationAndInvalidRangesDoNotCallNetwork()
    {
        var fixture = new Fixture(Wrap(Entry));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.GetPriceForecastAsync(Start, Start.AddHours(1), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.GetPriceForecastAsync(Start, Start));
        Assert.Empty(fixture.Handler.Bodies);
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public FakeControllerSettingStore Settings { get; } = new(ControllerSettingDefaults.CreateDefaultSettings(Start)
            .Where(s => s.Key is not (ControllerSettingDefaults.OctopusApiKeyKey or ControllerSettingDefaults.OctopusAccountNumberKey))
            .Concat([
                new(ControllerSettingDefaults.OctopusApiKeyKey, "test-api-key", ControllerSettingSensitivity.Sensitive, Start),
                new(ControllerSettingDefaults.OctopusAccountNumberKey, "A-TEST", ControllerSettingSensitivity.Normal, Start)
            ]));
        public Handler Handler { get; }
        public OctopusPriceForecastProvider Provider { get; }
        public Fixture(string priceResponse)
        {
            Handler = new Handler(priceResponse);
            Provider = new OctopusPriceForecastProvider(new HttpClient(Handler), Settings, new OctopusPriceCache(), Clock);
        }
        public SelectedElectricityPriceForecastProvider Selector() =>
            new(Settings, new TibberPriceForecastProvider(new HttpClient(Handler), Settings), Provider,
                new EnergyChartsPriceForecastProvider(new HttpClient(Handler), Settings, Clock, new()));
    }

    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start;
    }

    private sealed class Handler(string priceResponse) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string?> Headers { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(OctopusPriceForecastProvider.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            var body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            Headers.Add(request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null);
            return new HttpResponseMessage(Status) { Content = new StringContent(body.Contains("obtainKrakenToken") ? Token : priceResponse) };
        }
    }
}
