using TibberVictronController.Api.Dashboard;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class DashboardOverviewDtoTests
{
    [Fact]
    public void JointOverviewDoesNotOverwritePrimaryControlOrMeasurements()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var summary = new DashboardSummary(now, new(DashboardActionCategory.Idle, 0, null, "Victron steht", .27m, "EUR", now), null, [], [], []) {
            Overview = new(DashboardActionCategory.Discharge, "E3/DC deckt Verbrauch", .27m, "EUR",
                new(now.AddHours(1), DashboardActionCategory.GridCharge, "Günstigeres Ladefenster", .15m, "EUR", false))
        };
        var dto = DashboardSummaryDtoMapper.Map(summary);
        Assert.Equal("idle", dto.Current.Category);
        Assert.Equal(0, dto.Current.TargetPowerWatts);
        Assert.Equal("discharge", dto.Overview!.Category);
        Assert.Equal(.27m, dto.Overview.PricePerKwh);
        Assert.Equal("gridCharge", dto.Overview.Next!.Category);
        Assert.Equal("Günstigeres Ladefenster", dto.Overview.Next.Reason);
    }
}
