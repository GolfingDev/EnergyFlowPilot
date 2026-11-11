using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.Tests;

public sealed class PriceTimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddHours(-12);
    private static readonly DateTimeOffset Until = Now.AddHours(48);

    [Fact]
    public void PastPublishedAndForecastAreMarkedByKind()
    {
        // Tariff = (spot + 0.15) x 1.19; supplier prices from now until midnight (13 h), the forecast continues gap-free.
        var past = Quarters(-48, 48, estimated: false);
        var upcoming = Quarters(0, 52, estimated: false);
        var snapshots = Enumerable.Range(-48, 100).Select(i => new PriceForecastSnapshot(At(i), PriceForecastSnapshot.Actual, Spot(i), Now))
            .Concat(Enumerable.Range(52, 400).Select(i => new PriceForecastSnapshot(At(i), 0, 0.05m, Now)))
            .ToArray();

        var timeline = PriceTimeline.Build(new PriceTimelineInput(Now, From, Until, past, upcoming, snapshots));

        Assert.Equal(At(52), timeline.ForecastFromUtc);
        Assert.All(timeline.Slots.Where(s => s.EndsAtUtc <= Now), slot => Assert.Equal(PriceTimelineKind.Past, slot.Kind));
        Assert.All(timeline.Slots.Where(s => s.StartsAtUtc >= Now && s.StartsAtUtc < At(52)), slot => Assert.Equal(PriceTimelineKind.Published, slot.Kind));
        var forecast = timeline.Slots.Where(s => s.Kind == PriceTimelineKind.Forecast).ToArray();
        Assert.Equal(192 - 52, forecast.Length);
        Assert.All(forecast, slot => Assert.Equal((0.05 + 0.15) * 1.19, slot.PricePerKwh, 3));
        Assert.Equal(From, timeline.Slots[0].StartsAtUtc);
        Assert.Equal(Until, timeline.Slots[^1].EndsAtUtc);
    }

    [Fact]
    public void SupplierPricesWinOverHistoryAndEstimatedOnesStayMarked()
    {
        // The history holds the current hour too; the supplier's slot replaces it. Estimated supplier slots are named so.
        var past = Quarters(-4, 6, estimated: false);
        var upcoming = Quarters(0, 4, estimated: false).Concat(Quarters(4, 4, estimated: true)).ToArray();

        var timeline = PriceTimeline.Build(new PriceTimelineInput(Now, From, Until, past, upcoming, []));

        Assert.Equal(12, timeline.Slots.Count);
        Assert.Equal(4, timeline.Slots.Count(s => s.Kind == PriceTimelineKind.Past));
        Assert.Equal(4, timeline.Slots.Count(s => s.Kind == PriceTimelineKind.Estimated));
        Assert.Null(timeline.ForecastFromUtc);
    }

    private static DateTimeOffset At(int quarter) => Now.AddMinutes(15 * quarter);

    private static decimal Spot(int quarter) => 0.06m + (quarter + 100) % 8 * 0.01m;

    private static TibberPriceForecastSlot[] Quarters(int first, int count, bool estimated) =>
        Enumerable.Range(first, count)
            .Select(i => new TibberPriceForecastSlot(new ForecastTimeSlot(At(i), At(i + 1)), (Spot(i) + 0.15m) * 1.19m, "EUR") { IsEstimated = estimated })
            .ToArray();
}
