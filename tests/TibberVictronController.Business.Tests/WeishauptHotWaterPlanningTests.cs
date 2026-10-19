using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.Tests;

public sealed class WeishauptHotWaterPlanningTests
{
    // Noon in Europe/Berlin, with the default demand at 18:00 (16:00 UTC).
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly WeishauptHotWaterSettings Settings = new() { IsEnabled = true };

    [Fact]
    public void RelativePlanningAccepts25CentsWhenEveningIs40Cents()
    {
        var prices = Prices(t => t >= Now.AddHours(2) && t < Now.AddHours(3) ? 0.25m : 0.40m);
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings, Now.AddHours(2), prices, []);
        Assert.Equal(Now.AddHours(2), plan.StartsAtUtc);
        Assert.Equal(Now.AddHours(3), plan.EndsAtUtc);
        Assert.Equal(0.1125m, plan.EstimatedAdvantagePerKwh);
        Assert.True(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now.AddHours(2), false, prices[8], null, plan));
    }

    [Fact]
    public void EqualCostsPreferLaterWindowAndDoNotStartEarlier()
    {
        var prices = Prices(t => (t >= Now.AddHours(2) && t < Now.AddHours(3)) ||
            (t >= Now.AddHours(4) && t < Now.AddHours(5)) ? 0.15m : 0.40m);
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings, Now, prices, []);
        Assert.Equal(Now.AddHours(4), plan.StartsAtUtc);
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now.AddHours(2), false, prices[8], null, plan));
    }

    [Fact]
    public void SmallSpreadDoesNotCoverEstimatedLosses()
    {
        var prices = Prices(t => t < Now.AddHours(6) ? 0.19m : 0.20m);
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings, Now, prices, []);
        Assert.True(plan.EstimatedAdvantagePerKwh < 0m);
        Assert.False(WeishauptHotWaterPolicy.ShouldBoost(Settings, Now.AddHours(5), false, prices[20], null, plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrEstimatedFuturePricePreventsPriceBoost(bool estimated)
    {
        var prices = Prices(_ => 0.10m).ToArray();
        IReadOnlyList<TibberPriceForecastSlot> inputs = estimated
            ? prices.Select((p, i) => i == 20 ? p with { IsEstimated = true } : p).ToArray()
            : prices.Where((_, i) => i != 20).ToArray();
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings, Now, inputs, []);
        Assert.Null(plan.StartsAtUtc);
    }

    [Fact]
    public void ObservedEarlyReserveLossShortensFuturePlanningHorizon()
    {
        var episode = Episode();
        var analyses = new[] { 1m, 2m, 3m }.Select(hours => new WeishauptHotWaterEpisodeAnalysis(
            episode, Now.AddHours(1), null, Now.AddHours((double)(1m + hours)), 44m, false, false, "Reserve verloren")).ToArray();
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings, Now, Prices(_ => 0.10m), analyses);
        Assert.Equal(1m, plan.HoldHours);
        Assert.Equal(75, plan.ChargeMinutes);
        Assert.NotNull(plan.StartsAtUtc);
    }

    [Fact]
    public void IncompleteHistoryDoesNotChangeInitialAssumptions()
    {
        var history = Enumerable.Range(0, 4).Select(_ => new WeishauptHotWaterEpisodeAnalysis(
            Episode(), Now.AddMinutes(5), null, Now.AddMinutes(10), null, null, true, "Messluecken")).ToArray();
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings, Now, Prices(_ => 0.10m), history);
        Assert.Equal(6m, plan.HoldHours);
        Assert.Equal(60, plan.ChargeMinutes);
    }

    [Theory]
    [InlineData("2026-03-28T12:00:00+00:00", "2026-03-29T01:00:00+00:00")]
    [InlineData("2026-10-24T12:00:00+00:00", "2026-10-25T01:00:00+00:00")]
    public void DemandHourHandlesBerlinClockChanges(string now, string expected)
    {
        var plan = WeishauptHotWaterPricePlanner.Plan(Settings with { DemandHourLocal = 2 }, DateTimeOffset.Parse(now), [], []);
        Assert.Equal(DateTimeOffset.Parse(expected), plan.NeedByUtc);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ObservedEveningAvailabilityRequiresNoSubsequentCharge(bool reheat, bool expected)
    {
        var samples = Samples().Select(s => reheat && s.MeasuredAtUtc == Now.AddHours(4)
            ? new WeishauptBiblockTelemetrySample { MeasuredAtUtc = s.MeasuredAtUtc, HotWaterTemperatureCelsius = 48m,
                OutdoorTemperatureCelsius = 10m, OperatingStatusCode = 20 } : s).ToArray();
        var analysis = Assert.Single(WeishauptHotWaterEpisodeAnalyzer.Analyze([Episode()], samples, Now.AddHours(7)));
        Assert.Equal(expected, analysis.BridgedToNeed);
        Assert.Equal(47.5m, analysis.TemperatureAtNeedCelsius);
        Assert.Equal(Now.AddHours(1), analysis.TargetReachedAtUtc);
    }

    [Fact]
    public void GapsAndUnconfirmedWritesAreNeverReportedAsSuccess()
    {
        var samples = Samples().Where(s => s.MeasuredAtUtc < Now.AddHours(3) || s.MeasuredAtUtc > Now.AddHours(4)).ToArray();
        var analysis = Assert.Single(WeishauptHotWaterEpisodeAnalyzer.Analyze([Episode()], samples, Now.AddHours(7)));
        Assert.True(analysis.HasDataGaps);
        Assert.Null(analysis.BridgedToNeed);
        analysis = Assert.Single(WeishauptHotWaterEpisodeAnalyzer.Analyze([Episode() with { WriteConfirmed = false }], Samples(), Now.AddHours(7)));
        Assert.Null(analysis.BridgedToNeed);
    }

    private static IReadOnlyList<TibberPriceForecastSlot> Prices(Func<DateTimeOffset, decimal> price) =>
        Enumerable.Range(0, 32).Select(i => new TibberPriceForecastSlot(new ForecastTimeSlot(Now.AddMinutes(i * 15),
            Now.AddMinutes((i + 1) * 15)), price(Now.AddMinutes(i * 15)), "EUR")).ToArray();

    private static WeishauptHotWaterEpisode Episode() => new()
    {
        StartedAtUtc = Now, EndedAtUtc = Now.AddHours(1), OriginalCelsius = 45m, RaisedCelsius = 50m,
        WriteConfirmed = true, Trigger = "Preisfenster",
        Plan = new(Now.AddHours(6), Now, Now.AddHours(1), 0.10m, 0.40m, 0.285m, 6m, 60, "Preisfenster")
    };

    private static IReadOnlyList<WeishauptBiblockTelemetrySample> Samples() => Enumerable.Range(0, 85)
        .Select(i => new WeishauptBiblockTelemetrySample
        {
            MeasuredAtUtc = Now.AddMinutes(i * 5), OutdoorTemperatureCelsius = 10m,
            HotWaterTemperatureCelsius = i < 12 ? 45m : 50m - (i - 12) * 5m / 60m * 0.5m,
            OperatingStatusCode = i < 12 ? (ushort)20 : (ushort)19
        }).ToArray();
}
