using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.Tests;

public sealed class PriceForecastAccuracyTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    // 2026-11-10 00:00 local time (UTC+1).
    private static readonly DateTimeOffset Day = new(2026, 11, 9, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LeadTimeIsCountedInWholeDaysAndPublishedPricesAreTheReference()
    {
        var captured = Day.AddHours(-30);
        var snapshots = PriceForecastAccuracy.Snapshots([
            new PriceForecastPoint(Day, 0.20m, true),
            new PriceForecastPoint(Day.AddHours(-24), 0.25m, false)
        ], captured);

        Assert.Equal(1, snapshots[0].LeadDays);
        Assert.Equal(PriceForecastSnapshot.Actual, snapshots[1].LeadDays);
    }

    [Fact]
    public void ErrorBiasAndCostOfTheForecastCheapestHoursPerLeadTime()
    {
        // Actual: cheap in the first four hours (0.10), else 0.30. Forecast two days ahead: everything 1 ct too high,
        // so it finds the right hours; one day ahead it expects the cheap hours at the end of the day.
        var actual = Quarters(index => index < 16 ? 0.10m : 0.30m).Select(slot => new PriceForecastSnapshot(slot.Start, PriceForecastSnapshot.Actual, slot.Price, Day));
        var twoDays = Quarters(index => (index < 16 ? 0.10m : 0.30m) + 0.01m).Select(slot => new PriceForecastSnapshot(slot.Start, 2, slot.Price, Day.AddDays(-2)));
        var oneDay = Quarters(index => index >= 80 ? 0.10m : 0.30m).Select(slot => new PriceForecastSnapshot(slot.Start, 1, slot.Price, Day.AddDays(-1)));

        var rows = PriceForecastAccuracy.Evaluate([.. actual, .. twoDays, .. oneDay], Berlin);

        Assert.Equal([1, 2], rows.Select(row => row.LeadDays));
        var good = rows[1];
        Assert.Equal(1, good.MeanAbsoluteErrorCent, 3);
        Assert.Equal(1, good.BiasCent, 3);
        Assert.Equal(0, good.CheapestHoursExtraCostCent!.Value, 3);
        Assert.Equal(1, good.Days);
        // Wrong hours: charging there costs 20 ct/kWh more than in the really cheapest four hours.
        Assert.Equal(20, rows[0].CheapestHoursExtraCostCent!.Value, 3);
    }

    private static IEnumerable<(DateTimeOffset Start, decimal Price)> Quarters(Func<int, decimal> price) =>
        Enumerable.Range(0, 96).Select(index => (Day.AddMinutes(15 * index), price(index)));
}
