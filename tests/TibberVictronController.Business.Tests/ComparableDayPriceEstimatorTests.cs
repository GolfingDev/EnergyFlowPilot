using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class ComparableDayPriceEstimatorTests
{
    private static readonly TimeZoneInfo Berlin = ResolveBerlin();

    [Theory]
    [InlineData("2026-10-16", "2026-10-15")]
    [InlineData("2026-10-13", "2026-10-12")]
    [InlineData("2026-10-17", "2026-10-10")]
    [InlineData("2026-10-18", "2026-10-11")]
    [InlineData("2026-10-19", "2026-10-12")]
    public void GetComparableDayUsesPreviousWorkdayOrSameWeekdayOfLastWeek(string targetDay, string expectedSourceDay)
    {
        var sourceDay = ComparableDayPriceEstimator.GetComparableDay(DateTime.Parse(targetDay));

        Assert.Equal(DateTime.Parse(expectedSourceDay), sourceDay);
    }

    [Fact]
    public async Task CompleteAsyncEstimatesFridayWithThursdayPricesFromTibber()
    {
        // Thursday 2026-10-15 before 13:00: Tibber only knows Thursday, the plan must reach into Friday morning.
        var thursdayStartUtc = LocalToUtc(new DateTime(2026, 10, 15));
        var tibber = new DayPriceProvider(thursdayStartUtc, slot => 0.20m + TimeZoneInfo.ConvertTime(slot, Berlin).Hour / 100m);
        var knownSlots = (await tibber.GetPriceForecastAsync(thursdayStartUtc, thursdayStartUtc.AddDays(1)))
            .Where(slot => slot.TimeSlot.StartsAtUtc >= LocalToUtc(new DateTime(2026, 10, 15, 22, 0, 0)))
            .ToArray();
        var estimator = new ComparableDayPriceEstimator(tibber, new LoggedPriceRepository(null), Berlin);

        var completed = await estimator.CompleteAsync(
            knownSlots,
            LocalToUtc(new DateTime(2026, 10, 16, 6, 0, 0)),
            thursdayStartUtc.AddHours(10));

        var estimated = completed.Where(slot => slot.IsEstimated).ToArray();
        Assert.Equal(24, estimated.Length);
        Assert.Equal(0.20m, estimated[0].TotalPricePerKwh);
        Assert.Equal(0.25m, estimated[^1].TotalPricePerKwh);
        Assert.All(knownSlots, slot => Assert.False(slot.IsEstimated));
    }

    [Fact]
    public async Task CompleteAsyncEstimatesMondayFromDecisionLogOfPreviousMonday()
    {
        var sundayEveningUtc = LocalToUtc(new DateTime(2026, 10, 18, 23, 0, 0));
        var knownSlots = new[]
        {
            new TibberPriceForecastSlot(new ForecastTimeSlot(sundayEveningUtc, sundayEveningUtc.AddMinutes(15)), 0.30m, "EUR"),
            new TibberPriceForecastSlot(new ForecastTimeSlot(sundayEveningUtc.AddMinutes(15), sundayEveningUtc.AddMinutes(30)), 0.30m, "EUR"),
            new TibberPriceForecastSlot(new ForecastTimeSlot(sundayEveningUtc.AddMinutes(30), sundayEveningUtc.AddMinutes(45)), 0.30m, "EUR"),
            new TibberPriceForecastSlot(new ForecastTimeSlot(sundayEveningUtc.AddMinutes(45), sundayEveningUtc.AddMinutes(60)), 0.30m, "EUR")
        };
        var estimator = new ComparableDayPriceEstimator(
            new DayPriceProvider(sundayEveningUtc.AddDays(-30), _ => 0m),
            new LoggedPriceRepository(-0.05m),
            Berlin);

        var completed = await estimator.CompleteAsync(knownSlots, sundayEveningUtc.AddHours(3), sundayEveningUtc);

        var estimated = completed.Where(slot => slot.IsEstimated).ToArray();
        Assert.Equal(8, estimated.Length);
        Assert.All(estimated, slot => Assert.Equal(0m, slot.TotalPricePerKwh));
    }

    [Theory]
    [InlineData("octopusGermany", "{}")]
    [InlineData("tibber", "{\"ElectricityPriceProvider\":\"octopusGermany\"}")]
    public async Task NeverUsesOtherSuppliersHistoricalPrices(string provider, string historicalSource)
    {
        var start = LocalToUtc(new DateTime(2026, 11, 22, 23, 45, 0));
        var known = new[] { new TibberPriceForecastSlot(new ForecastTimeSlot(start, start.AddMinutes(15)), 0.3m, "EUR") };
        var prices = new DayPriceProvider(start.AddDays(-30), _ => 0m) { Source = provider };
        var history = new LoggedPriceRepository(0.1m) { InputSummaryJson = historicalSource };
        var estimator = new ComparableDayPriceEstimator(prices, history, Berlin);
        var completed = await estimator.CompleteAsync(known, start.AddHours(2), start);
        Assert.Single(completed);
    }

    private static DateTimeOffset LocalToUtc(DateTime local)
    {
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Berlin), TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveBerlin()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    /// <summary>Delivers prices for exactly one local day, like Tibber before next-day prices are published.</summary>
    private sealed class DayPriceProvider : ITibberPriceForecastProvider
    {
        public string Source { get; init; } = "tibber";
        private readonly DateTimeOffset dayStartUtc;
        private readonly Func<DateTimeOffset, decimal> price;

        public DayPriceProvider(DateTimeOffset dayStartUtc, Func<DateTimeOffset, decimal> price)
        {
            this.dayStartUtc = dayStartUtc;
            this.price = price;
        }

        public Task<IReadOnlyList<TibberPriceForecastSlot>> GetPriceForecastAsync(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc, CancellationToken cancellationToken = default)
        {
            var slots = Enumerable.Range(0, 96)
                .Select(index => dayStartUtc.AddMinutes(15 * index))
                .Where(start => start >= startsAtUtc && start < endsAtUtc)
                .Select(start => new TibberPriceForecastSlot(new ForecastTimeSlot(start, start.AddMinutes(15)), price(start), "EUR"))
                .ToArray();

            return Task.FromResult<IReadOnlyList<TibberPriceForecastSlot>>(slots);
        }
    }

    /// <summary>Returns one logged decision with the given price for every requested range.</summary>
    private sealed class LoggedPriceRepository : IDecisionLogRepository
    {
        public string InputSummaryJson { get; init; } = "{}";
        private readonly decimal? price;

        public LoggedPriceRepository(decimal? price)
        {
            this.price = price;
        }

        public Task<IReadOnlyList<DecisionLogEntry>> GetDecisionsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int maxCount, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<DecisionLogEntry> entries = price is null
                ? Array.Empty<DecisionLogEntry>()
                : new[]
                {
                    new DecisionLogEntry(
                        Guid.NewGuid(),
                        fromUtc,
                        fromUtc,
                        fromUtc.AddMinutes(15),
                        new CurrentBatteryDecision(new BatteryDecisionInstruction(BatteryDecisionState.Idle, chargeSource: null), 0),
                        stateOfChargePercent: 50m,
                        tibberPricePerKwh: price,
                        tibberPriceCurrency: "EUR",
                        gridImportWatts: null,
                        gridExportWatts: null,
                        inputSummaryJson: InputSummaryJson,
                        new[] { new BatteryDecisionReason("TEST", "Test.") })
                };

            return Task.FromResult(entries);
        }

        public Task SaveDecisionAsync(DecisionLogEntry decisionLogEntry, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DecisionLogEntry>> GetRecentDecisionsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DecisionLogEntry>>(Array.Empty<DecisionLogEntry>());

        public Task<int> DeleteDecisionsOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
