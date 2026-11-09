using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Tibber;

namespace TibberVictronController.Dal.Tests;

public sealed class TibberLivePriceForecastProviderTests
{
    private const string AccessTokenEnvironmentVariable = "TIBBER_ACCESS_TOKEN";
    private const string ApiEndpointEnvironmentVariable = "TIBBER_API_ENDPOINT";
    private const string HomeSelectionEnvironmentVariable = "TIBBER_HOME_SELECTION";

    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [RequiresEnvironmentVariableFact(AccessTokenEnvironmentVariable)]
    public async Task GetPriceForecastAsyncLoadsRealQuarterHourlyPricesFromTibber()
    {
        var accessToken = EnvironmentVariableReader.GetValue(AccessTokenEnvironmentVariable)!;
        var apiEndpoint = EnvironmentVariableReader.GetValue(ApiEndpointEnvironmentVariable)
            ?? "https://api.tibber.com/v1-beta/gql";
        var homeSelection = EnvironmentVariableReader.GetValue(HomeSelectionEnvironmentVariable)
            ?? "first";
        var settingsStore = CreateSettingsStore(apiEndpoint, accessToken, homeSelection);
        var provider = new TibberPriceForecastProvider(new HttpClient(), settingsStore);
        var startsAtUtc = DateTimeOffset.UtcNow.AddMinutes(-15);
        var endsAtUtc = startsAtUtc.AddHours(12);

        var priceSlots = await provider.GetPriceForecastAsync(startsAtUtc, endsAtUtc);

        Assert.NotEmpty(priceSlots);
        Assert.All(priceSlots, priceSlot => Assert.True(priceSlot.TimeSlot.IsFifteenMinuteSlot));
        Assert.All(priceSlots, priceSlot => Assert.False(string.IsNullOrWhiteSpace(priceSlot.Currency)));
    }

    private static FakeControllerSettingStore CreateSettingsStore(
        string apiEndpoint,
        string accessToken,
        string homeSelection)
    {
        var settings = ControllerSettingDefaults.CreateDefaultSettings(UpdatedAtUtc).ToList();

        ReplaceSetting(settings, ControllerSettingDefaults.TibberApiEndpointKey, apiEndpoint, ControllerSettingSensitivity.Normal);
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
}
