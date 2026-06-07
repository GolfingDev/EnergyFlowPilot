using System.Net;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Tibber;

namespace TibberVictronController.Dal.Tests;

public sealed class TibberPriceForecastProviderTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetPriceForecastAsyncPostsGraphQlRequestAndMapsQuarterHourlyPrices()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "test-token");
        var provider = new TibberPriceForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var endsAtUtc = startsAtUtc.AddMinutes(30);

        var priceSlots = await provider.GetPriceForecastAsync(startsAtUtc, endsAtUtc);

        Assert.Equal(HttpMethod.Post, httpHandler.LastRequest?.Method);
        Assert.Equal("Bearer", httpHandler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", httpHandler.LastRequest?.Headers.Authorization?.Parameter);
        Assert.Contains("QUARTER_HOURLY", httpHandler.LastRequestBody);
        Assert.Equal(2, priceSlots.Count);
        Assert.All(priceSlots, priceSlot => Assert.True(priceSlot.TimeSlot.IsFifteenMinuteSlot));
        Assert.Equal(0.21m, priceSlots[0].TotalPricePerKwh);
        Assert.Equal("EUR", priceSlots[0].Currency);
    }

    [Fact]
    public async Task GetPriceForecastAsyncUsesConfiguredHomeSelection()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateMultiHomeResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "test-token", homeSelection: "home-b");
        var provider = new TibberPriceForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var endsAtUtc = startsAtUtc.AddMinutes(15);

        var priceSlots = await provider.GetPriceForecastAsync(startsAtUtc, endsAtUtc);

        Assert.Single(priceSlots);
        Assert.Equal(0.31m, priceSlots[0].TotalPricePerKwh);
    }

    [Fact]
    public async Task GetPriceForecastAsyncUsesCachedPricesForRepeatedCalls()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "test-token");
        var provider = new TibberPriceForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var firstSlots = await provider.GetPriceForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15));
        var secondSlots = await provider.GetPriceForecastAsync(startsAtUtc.AddMinutes(15), startsAtUtc.AddMinutes(30));

        Assert.Single(firstSlots);
        Assert.Single(secondSlots);
        Assert.Equal(1, httpHandler.RequestCount);
        Assert.Equal(0.22m, secondSlots[0].TotalPricePerKwh);
    }

    [Fact]
    public async Task GetPriceForecastAsyncUsesLastGoodPricesWhenRefreshFails()
    {
        var httpHandler = new SequencedHttpMessageHandler(
            new SequencedHttpResponse(HttpStatusCode.OK, CreateSuccessfulResponse()),
            new SequencedHttpResponse(HttpStatusCode.TooManyRequests, "{}"));
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "test-token");
        var forecastCache = new TibberPriceForecastCache();
        var firstProvider = new TibberPriceForecastProvider(httpClient, settingsStore, forecastCache, TimeSpan.FromMinutes(30));
        var secondProvider = new TibberPriceForecastProvider(httpClient, settingsStore, forecastCache, TimeSpan.Zero);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var firstSlots = await firstProvider.GetPriceForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15));
        var secondSlots = await secondProvider.GetPriceForecastAsync(startsAtUtc.AddMinutes(15), startsAtUtc.AddMinutes(30));

        Assert.Single(firstSlots);
        Assert.Single(secondSlots);
        Assert.Equal(2, httpHandler.RequestCount);
        Assert.Equal(0.22m, secondSlots[0].TotalPricePerKwh);
    }

    [Fact]
    public async Task GetPriceForecastAsyncRejectsMissingAccessToken()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: null);
        var provider = new TibberPriceForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetPriceForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15)));

        Assert.Contains("Der Tibber Access Token ist nicht konfiguriert.", exception.Message);
    }

    [Fact]
    public async Task GetPriceForecastAsyncRejectsGraphQlErrors()
    {
        var httpHandler = new RecordingHttpMessageHandler("""
            {
              "errors": [
                { "message": "Unauthorized" }
              ]
            }
            """);
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "bad-token");
        var provider = new TibberPriceForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<TibberApiException>(
            () => provider.GetPriceForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15)));

        Assert.Contains("Tibber hat Fehler zurueckgegeben", exception.Message);
        Assert.Contains("Unauthorized", exception.Message);
    }

    [Fact]
    public async Task GetPriceForecastAsyncRejectsNonQuarterHourlyPrices()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateHourlyResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(accessToken: "test-token");
        var provider = new TibberPriceForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<TibberApiException>(
            () => provider.GetPriceForecastAsync(startsAtUtc, startsAtUtc.AddHours(1)));

        Assert.Contains("Tibber-Preise muessen im 15-Minuten-Raster vorliegen.", exception.Message);
    }

    private static FakeControllerSettingStore CreateSettingsStore(
        string? accessToken,
        string homeSelection = "first")
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc).ToList();

        ReplaceSetting(settings, ControllerSettingDefaults.TibberAccessTokenKey, accessToken, ControllerSettingSensitivity.Sensitive);
        ReplaceSetting(settings, ControllerSettingDefaults.TibberHomeSelectionKey, homeSelection, ControllerSettingSensitivity.Normal);

        return new FakeControllerSettingStore(settings);
    }

    private static void ReplaceSetting(
        List<ControllerSetting> settings,
        string key,
        string? value,
        ControllerSettingSensitivity sensitivity)
    {
        settings.RemoveAll(setting => setting.Key == key);
        settings.Add(new ControllerSetting(key, value, sensitivity, UpdatedAtUtc));
    }

    private static string CreateSuccessfulResponse()
    {
        return """
            {
              "data": {
                "viewer": {
                  "homes": [
                    {
                      "id": "home-a",
                      "currentSubscription": {
                        "priceInfo": {
                          "current": { "total": 0.21, "startsAt": "2026-05-01T10:00:00.000+02:00", "currency": "EUR" },
                          "today": [
                            { "total": 0.21, "startsAt": "2026-05-01T10:00:00.000+02:00", "currency": "EUR" },
                            { "total": 0.22, "startsAt": "2026-05-01T10:15:00.000+02:00", "currency": "EUR" }
                          ],
                          "tomorrow": []
                        }
                      }
                    }
                  ]
                }
              }
            }
            """;
    }

    private static string CreateMultiHomeResponse()
    {
        return """
            {
              "data": {
                "viewer": {
                  "homes": [
                    {
                      "id": "home-a",
                      "currentSubscription": {
                        "priceInfo": {
                          "current": null,
                          "today": [
                            { "total": 0.21, "startsAt": "2026-05-01T10:00:00.000+02:00", "currency": "EUR" }
                          ],
                          "tomorrow": []
                        }
                      }
                    },
                    {
                      "id": "home-b",
                      "currentSubscription": {
                        "priceInfo": {
                          "current": null,
                          "today": [
                            { "total": 0.31, "startsAt": "2026-05-01T10:00:00.000+02:00", "currency": "EUR" }
                          ],
                          "tomorrow": []
                        }
                      }
                    }
                  ]
                }
              }
            }
            """;
    }

    private static string CreateHourlyResponse()
    {
        return """
            {
              "data": {
                "viewer": {
                  "homes": [
                    {
                      "id": "home-a",
                      "currentSubscription": {
                        "priceInfo": {
                          "current": null,
                          "today": [
                            { "total": 0.21, "startsAt": "2026-05-01T10:00:00.000+02:00", "currency": "EUR" },
                            { "total": 0.22, "startsAt": "2026-05-01T11:00:00.000+02:00", "currency": "EUR" }
                          ],
                          "tomorrow": []
                        }
                      }
                    }
                  ]
                }
              }
            }
            """;
    }

    private sealed class SequencedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<SequencedHttpResponse> responses;

        public SequencedHttpMessageHandler(params SequencedHttpResponse[] responses)
        {
            this.responses = new Queue<SequencedHttpResponse>(responses);
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = responses.Count > 0
                ? responses.Dequeue()
                : throw new InvalidOperationException("Es ist keine weitere HTTP-Testantwort konfiguriert.");

            return Task.FromResult(new HttpResponseMessage(response.StatusCode)
            {
                Content = new StringContent(response.ResponseBody)
            });
        }
    }

    private sealed record SequencedHttpResponse(HttpStatusCode StatusCode, string ResponseBody);
}
