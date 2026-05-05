using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Weather;

namespace TibberVictronController.Dal.Tests;

public sealed class ForecastSolarPvForecastProviderTests
{
    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetPvYieldForecastAsyncBuildsPublicApiRequestAndMapsHourlyWattHoursToQuarterHours()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore();
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        var endsAtUtc = startsAtUtc.AddHours(1);

        var pvSlots = await provider.GetPvYieldForecastAsync(startsAtUtc, endsAtUtc);

        Assert.Equal(HttpMethod.Get, httpHandler.LastRequest?.Method);
        Assert.Equal("/estimate/52.52/13.405/35/0/10", httpHandler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Equal(4, pvSlots.Count);
        Assert.All(pvSlots, pvSlot => Assert.True(pvSlot.TimeSlot.IsFifteenMinuteSlot));
        Assert.All(pvSlots, pvSlot => Assert.Equal(0.125m, pvSlot.ExpectedPvYieldKwh));
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncUsesForecastSolarApiKeyWhenConfigured()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(apiKey: "paid-api-key");
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        await provider.GetPvYieldForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15));

        Assert.Equal("/paid-api-key/estimate/52.52/13.405/35/0/10", httpHandler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncNormalizesAzimuthToForecastSolarRange()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(azimuthDegrees: "225");
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        await provider.GetPvYieldForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15));

        Assert.Equal("/estimate/52.52/13.405/35/-135/10", httpHandler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncMapsMissingForecastSolarIntervalsToZeroYield()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore();
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 7, 0, 0, TimeSpan.Zero);
        var endsAtUtc = startsAtUtc.AddHours(5);

        var pvSlots = await provider.GetPvYieldForecastAsync(startsAtUtc, endsAtUtc);

        Assert.Equal(20, pvSlots.Count);
        Assert.Equal(0m, pvSlots[0].ExpectedPvYieldKwh);
        Assert.All(pvSlots.Skip(4).Take(4), pvSlot => Assert.Equal(0.125m, pvSlot.ExpectedPvYieldKwh));
        Assert.All(pvSlots.Skip(8), pvSlot => Assert.Equal(0m, pvSlot.ExpectedPvYieldKwh));
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncRejectsMissingRequiredSetting()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc).ToList();
        settings.RemoveAll(setting => setting.Key == ControllerSettingDefaults.PvForecastLatitudeKey);
        var settingsStore = new FakeControllerSettingStore(settings);
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetPvYieldForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15)));

        Assert.Contains("Die PV-Standort-Breitengrad-Einstellung ist nicht konfiguriert.", exception.Message);
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncRejectsInvalidDecimalSetting()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore(latitude: "ungueltig");
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetPvYieldForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15)));

        Assert.Contains("Die PV-Standort-Breitengrad-Einstellung muss als Dezimalzahl konfiguriert sein.", exception.Message);
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncRejectsForecastSolarApiError()
    {
        var httpHandler = new RecordingHttpMessageHandler("""
            {
              "result": {},
              "message": {
                "type": "error",
                "code": 1,
                "text": "Rate limit exceeded"
              }
            }
            """);
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore();
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var startsAtUtc = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<ForecastSolarApiException>(
            () => provider.GetPvYieldForecastAsync(startsAtUtc, startsAtUtc.AddMinutes(15)));

        Assert.Contains("Forecast.Solar hat Fehler zurueckgegeben", exception.Message);
        Assert.Contains("Rate limit exceeded", exception.Message);
    }

    [Fact]
    public async Task GetPvYieldForecastAsyncRejectsNonUtcRange()
    {
        var httpHandler = new RecordingHttpMessageHandler(CreateSuccessfulResponse());
        var httpClient = new HttpClient(httpHandler);
        var settingsStore = CreateSettingsStore();
        var provider = new ForecastSolarPvForecastProvider(httpClient, settingsStore);
        var localStart = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.FromHours(2));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => provider.GetPvYieldForecastAsync(localStart, localStart.AddMinutes(15)));

        Assert.Contains("Der Start des PV-Forecast-Zeitraums muss in UTC angegeben sein.", exception.Message);
    }

    private static FakeControllerSettingStore CreateSettingsStore(
        string latitude = "52.52",
        string azimuthDegrees = "0",
        string? apiKey = null)
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc).ToList();

        ReplaceSetting(settings, ControllerSettingDefaults.PvForecastLatitudeKey, latitude);
        ReplaceSetting(settings, ControllerSettingDefaults.PvForecastAzimuthDegreesKey, azimuthDegrees);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            ReplaceSetting(settings, ControllerSettingDefaults.PvForecastApiKeyKey, apiKey, ControllerSettingSensitivity.Sensitive);
        }

        return new FakeControllerSettingStore(settings);
    }

    private static void ReplaceSetting(
        List<ControllerSetting> settings,
        string key,
        string? value,
        ControllerSettingSensitivity sensitivity = ControllerSettingSensitivity.Normal)
    {
        settings.RemoveAll(setting => setting.Key == key);
        settings.Add(new ControllerSetting(key, value, sensitivity, UpdatedAtUtc));
    }

    private static string CreateSuccessfulResponse()
    {
        return """
            {
              "result": {
                "watt_hours": {
                  "2026-05-01 10:00:00": 1000,
                  "2026-05-01 11:00:00": 1500
                }
              },
              "message": {
                "type": "success",
                "code": 0,
                "text": ""
              }
            }
            """;
    }
}
