using TibberVictronController.Business.Models;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Tibber;
using Xunit.Abstractions;

namespace TibberVictronController.Dal.Tests;

public sealed class TibberLivePriceForecastProviderTests
{
    private const string AccessTokenEnvironmentVariable = "TIBBER_ACCESS_TOKEN";
    private const string ApiEndpointEnvironmentVariable = "TIBBER_API_ENDPOINT";
    private const string HomeSelectionEnvironmentVariable = "TIBBER_HOME_SELECTION";

    private static readonly DateTimeOffset UpdatedAtUtc = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ITestOutputHelper testOutput;

    public TibberLivePriceForecastProviderTests(ITestOutputHelper testOutput)
    {
        this.testOutput = testOutput;
    }

    [RequiresEnvironmentVariableFact(AccessTokenEnvironmentVariable)]
    public async Task GetPriceForecastAsyncLoadsRealQuarterHourlyPricesFromTibber()
    {
        var accessToken = Environment.GetEnvironmentVariable(AccessTokenEnvironmentVariable)!;
        var apiEndpoint = Environment.GetEnvironmentVariable(ApiEndpointEnvironmentVariable)
            ?? "https://api.tibber.com/v1-beta/gql";
        var homeSelection = Environment.GetEnvironmentVariable(HomeSelectionEnvironmentVariable)
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

    [RequiresEnvironmentVariableFact(AccessTokenEnvironmentVariable)]
    public async Task EvaluateUsesRealTibberPricesForCurrentDecisionScenario()
    {
        var accessToken = Environment.GetEnvironmentVariable(AccessTokenEnvironmentVariable)!;
        var apiEndpoint = Environment.GetEnvironmentVariable(ApiEndpointEnvironmentVariable)
            ?? "https://api.tibber.com/v1-beta/gql";
        var homeSelection = Environment.GetEnvironmentVariable(HomeSelectionEnvironmentVariable)
            ?? "first";
        var settingsStore = CreateSettingsStore(apiEndpoint, accessToken, homeSelection);
        var priceProvider = new TibberPriceForecastProvider(new HttpClient(), settingsStore);
        var decisionTimeUtc = DateTimeOffset.UtcNow;
        var priceSlots = await priceProvider.GetPriceForecastAsync(
            decisionTimeUtc.AddHours(-1),
            decisionTimeUtc.AddHours(12));
        var currentPriceSlot = priceSlots.Single(priceSlot =>
            priceSlot.TimeSlot.StartsAtUtc <= decisionTimeUtc &&
            decisionTimeUtc < priceSlot.TimeSlot.EndsAtUtc);
        var batteryState = new BatteryState(55m, decisionTimeUtc);
        var pvYieldSlot = new PvYieldForecastSlot(currentPriceSlot.TimeSlot, expectedPvYieldKwh: 0.00m);
        var consumptionSlot = new ConsumptionForecastSlot(currentPriceSlot.TimeSlot, expectedConsumptionKwh: 0.30m);
        var evaluator = new BatteryDecisionRuleEvaluator();

        var result = evaluator.Evaluate(
            decisionTimeUtc,
            priceSlots,
            pvYieldSlot,
            consumptionSlot,
            batteryState);

        testOutput.WriteLine($"Zeitfenster UTC: {currentPriceSlot.TimeSlot.StartsAtUtc:o} bis {currentPriceSlot.TimeSlot.EndsAtUtc:o}");
        testOutput.WriteLine($"Aktueller Tibber-Preis: {currentPriceSlot.TotalPricePerKwh:0.0000} {currentPriceSlot.Currency}/kWh");
        testOutput.WriteLine($"Annahme SOC: {batteryState.StateOfChargePercent:0.#} Prozent");
        testOutput.WriteLine($"Annahme PV-Ertrag: {pvYieldSlot.ExpectedPvYieldKwh:0.0000} kWh");
        testOutput.WriteLine($"Annahme Verbrauch: {consumptionSlot.ExpectedConsumptionKwh:0.0000} kWh");
        testOutput.WriteLine($"Decision Engine Ergebnis: {result.Instruction.DecisionState}");
        testOutput.WriteLine($"Ladequelle: {result.Instruction.ChargeSource?.ToString() ?? "keine"}");
        testOutput.WriteLine($"Begruendung: {string.Join(" | ", result.Reasons.Select(reason => reason.Message))}");

        Assert.NotEmpty(result.Reasons);
    }

    [RequiresEnvironmentVariableFact(AccessTokenEnvironmentVariable)]
    public async Task EvaluatePrintsRealTibberForecastScenario()
    {
        var accessToken = Environment.GetEnvironmentVariable(AccessTokenEnvironmentVariable)!;
        var apiEndpoint = Environment.GetEnvironmentVariable(ApiEndpointEnvironmentVariable)
            ?? "https://api.tibber.com/v1-beta/gql";
        var homeSelection = Environment.GetEnvironmentVariable(HomeSelectionEnvironmentVariable)
            ?? "first";
        var settingsStore = CreateSettingsStore(apiEndpoint, accessToken, homeSelection);
        var priceProvider = new TibberPriceForecastProvider(new HttpClient(), settingsStore);
        var forecastStartsAtUtc = AlignDownToQuarterHour(DateTimeOffset.UtcNow);
        var forecastEndsAtUtc = forecastStartsAtUtc.AddHours(36);
        var priceSlots = await priceProvider.GetPriceForecastAsync(forecastStartsAtUtc, forecastEndsAtUtc);
        var evaluator = new BatteryDecisionRuleEvaluator();
        var batteryState = new BatteryState(55m, forecastStartsAtUtc);

        testOutput.WriteLine("Forecast-Annahmen:");
        testOutput.WriteLine("SOC konstant: 55 Prozent");
        testOutput.WriteLine("PV-Ertrag je Slot: 0,0000 kWh");
        testOutput.WriteLine("Verbrauch je Slot: 0,3000 kWh");
        testOutput.WriteLine("");
        testOutput.WriteLine("UTC Start | UTC Ende | Preis EUR/kWh | Entscheidung | Quelle | Begruendung");

        foreach (var priceSlot in priceSlots)
        {
            var pvYieldSlot = new PvYieldForecastSlot(priceSlot.TimeSlot, expectedPvYieldKwh: 0.00m);
            var consumptionSlot = new ConsumptionForecastSlot(priceSlot.TimeSlot, expectedConsumptionKwh: 0.30m);
            var result = evaluator.Evaluate(
                priceSlot.TimeSlot.StartsAtUtc,
                priceSlots,
                pvYieldSlot,
                consumptionSlot,
                batteryState);
            var chargeSource = result.Instruction.ChargeSource?.ToString() ?? "-";
            var reason = string.Join(" | ", result.Reasons.Select(decisionReason => decisionReason.Message));

            testOutput.WriteLine(
                $"{priceSlot.TimeSlot.StartsAtUtc:HH:mm} | {priceSlot.TimeSlot.EndsAtUtc:HH:mm} | {priceSlot.TotalPricePerKwh:0.0000} | {result.Instruction.DecisionState} | {chargeSource} | {reason}");
        }

        Assert.NotEmpty(priceSlots);
    }

    private static DateTimeOffset AlignDownToQuarterHour(DateTimeOffset timestampUtc)
    {
        var minute = timestampUtc.Minute - timestampUtc.Minute % 15;

        return new DateTimeOffset(
            timestampUtc.Year,
            timestampUtc.Month,
            timestampUtc.Day,
            timestampUtc.Hour,
            minute,
            0,
            TimeSpan.Zero);
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
