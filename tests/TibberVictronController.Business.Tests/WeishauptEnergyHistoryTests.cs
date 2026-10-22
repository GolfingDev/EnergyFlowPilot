using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.Tests;

public sealed class WeishauptEnergyHistoryTests
{
    [Fact]
    public async Task ComparesEqualPeriodsUsingFirstHourlyReadingsAndPreservesRealZero()
    {
        var now = DateTimeOffset.Parse("2026-10-22T12:34:00Z");
        var midnightUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var data = new Data(now);
        for (var i = 0; i <= 48; i++) data.Samples.Add(Sample(midnightUtc.AddDays(-2).AddHours(12 + i).AddMinutes(5), (ushort)(100 + i)));
        // A later reading in the same hour must not replace the boundary reading.
        data.Samples.Add(Sample(DateTimeOffset.Parse("2026-10-22T11:55:00Z"), 500));
        var report = await new WeishauptEnergyHistoryService(data, data).GetAsync(1, null, default);
        Assert.Equal(DateTimeOffset.Parse("2026-10-22T12:00:00Z"), report.EndsAtUtc);
        Assert.Equal(24, report.Hours.Count);
        Assert.Equal(24, report.PreviousHours.Count);
        Assert.All(report.Hours.Concat(report.PreviousHours), h => Assert.Equal(1m, h.ElectricalKwh));
        Assert.Equal(5, report.Hours[0].MeasuredFromUtc!.Value.Minute);
        Assert.Equal(8m, report.Hours[0].OutdoorTemperatureCelsius);
        data.Samples.Clear();
        data.Samples.Add(Sample(midnightUtc.AddHours(10), 10));
        data.Samples.Add(Sample(midnightUtc.AddHours(11), 10));
        report = await new WeishauptEnergyHistoryService(data, data).GetAsync(1, null, default);
        Assert.Equal(0m, report.Hours[^2].ElectricalKwh);
        Assert.Null(report.Hours[^1].ElectricalKwh);
    }

    [Fact]
    public async Task MissingCountersResetsAndOutagesRemainNullRatherThanConsumption()
    {
        var data = new Data(DateTimeOffset.Parse("2026-10-22T12:00:00Z"));
        data.Samples.AddRange([
            Sample(data.UtcNow.AddHours(-6), 50),
            Sample(data.UtcNow.AddHours(-5), null),
            Sample(data.UtcNow.AddHours(-4), 52),
            Sample(data.UtcNow.AddHours(-3), 1),
            Sample(data.UtcNow.AddHours(-1), 9),
            Sample(data.UtcNow, 10)
        ]);
        var report = await new WeishauptEnergyHistoryService(data, data).GetAsync(1, null, default);
        Assert.All(report.Hours.Take(23), h => Assert.Null(h.ElectricalKwh));
        Assert.Equal(1m, report.Hours[^1].ElectricalKwh);
    }

    [Theory]
    [InlineData("2026-10-21T22:00:00Z", 100, 102)] // Berlin midnight; annual counter remains valid.
    [InlineData("2026-11-01T00:00:00Z", 100, 102)] // Month boundary must not reset annual differences.
    public async Task AnnualCounterSurvivesDailyAndMonthlyRollover(string end, ushort before, ushort after)
    {
        var data = new Data(DateTimeOffset.Parse(end));
        data.Samples.Add(Sample(data.UtcNow.AddHours(-1), before, 20));
        data.Samples.Add(Sample(data.UtcNow, after, 1));
        var report = await new WeishauptEnergyHistoryService(data, data).GetAsync(1, null, default);
        Assert.Equal(2m, report.Hours[^1].ElectricalKwh);
    }

    [Fact]
    public async Task YearRolloverUsesFinalDailyCounterAndDoesNotSubtractAnnualReset()
    {
        var data = new Data(DateTimeOffset.Parse("2026-12-31T23:00:00Z"));
        data.Samples.Add(Sample(data.UtcNow.AddHours(-1), 5000, 20));
        data.Samples.Add(Sample(data.UtcNow, 1, 1, 22));
        var report = await new WeishauptEnergyHistoryService(data, data).GetAsync(1, null, default);
        Assert.Equal(3m, report.Hours[^1].ElectricalKwh);
    }

    [Fact]
    public async Task DaylightSavingFallbackRetainsBothUtcHoursAndFutureReadingsAreIgnored()
    {
        var data = new Data(DateTimeOffset.Parse("2026-10-25T04:00:00Z"));
        for (var i = 0; i <= 4; i++) data.Samples.Add(Sample(data.UtcNow.AddHours(i - 4), (ushort)(100 + i)));
        var report = await new WeishauptEnergyHistoryService(data, data).GetAsync(1, data.UtcNow.AddHours(-1), default);
        Assert.Equal(24, report.Hours.Count);
        Assert.Equal(3, report.Hours.Count(h => h.ElectricalKwh.HasValue));
        Assert.Equal(data.UtcNow.AddHours(-1), report.Hours[^1].MeasuredToUtc);
    }

    [Fact]
    public async Task BadRangeAndFutureEndAreRejected()
    {
        var data = new Data(DateTimeOffset.Parse("2026-10-22T12:00:00Z"));
        var service = new WeishauptEnergyHistoryService(data, data);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(2, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync(1, data.UtcNow.AddHours(1), default));
    }

    private static WeishauptBiblockTelemetrySample Sample(DateTimeOffset time, ushort? year, ushort? today = null, ushort? yesterday = null) => new()
    {
        MeasuredAtUtc = time, OutdoorTemperatureCelsius = 8m, HotWaterTemperatureCelsius = 45m,
        OperatingStatusCode = 19, ElectricalYearKwh = year, ElectricalTodayKwh = today, ElectricalYesterdayKwh = yesterday
    };

    private sealed class Data(DateTimeOffset now) : IWeishauptBiblockTelemetryRepository, IUtcClock
    {
        public DateTimeOffset UtcNow => now;
        public List<WeishauptBiblockTelemetrySample> Samples { get; } = [];
        public Task<IReadOnlyList<WeishauptBiblockTelemetrySample>> GetSamplesAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WeishauptBiblockTelemetrySample>>(Samples.Where(s => s.MeasuredAtUtc >= start && s.MeasuredAtUtc < end).ToArray());
        public Task SaveSampleAsync(WeishauptBiblockTelemetrySample sample, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset threshold, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
