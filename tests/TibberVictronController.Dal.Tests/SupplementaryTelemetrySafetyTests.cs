using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.HagerEnergy;

namespace TibberVictronController.Dal.Tests;

public sealed class SupplementaryTelemetrySafetyTests
{
    [Fact]
    public void RecentPollDoesNotMakeAnOldCloudMeasurementFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new HagerEnergyTelemetrySnapshot(27, 3090, 51, -774, now.AddMinutes(-10), now);
        var telemetry = SelectedCurrentSiteTelemetryProvider.WithSupplementaryTelemetry(new CurrentSiteTelemetry(27, 3090, now, 3030), snapshot, true);
        Assert.Equal(now.AddMinutes(-10), telemetry.SupplementaryBatteryMeasuredAtUtc);
        Assert.Equal(774, telemetry.SupplementaryBatteryDischargeWatts);
        Assert.Equal(CurrentBatteryDecisionRuleIds.SupplementaryTelemetryMissing, new SupplementaryGridChargeGuard().Evaluate(telemetry, now)?.RuleName);
    }

    [Fact]
    public void MissingPowerIsUnknownButAnExplicitZeroIsValid()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new HagerEnergyTelemetrySnapshot(27, 3090, 51, null, now, now);
        var telemetry = new CurrentSiteTelemetry(27, 3090, now, 3030);
        var missing = SelectedCurrentSiteTelemetryProvider.WithSupplementaryTelemetry(telemetry, snapshot, true);
        Assert.Null(missing.SupplementaryBatteryMeasuredAtUtc);
        Assert.NotNull(new SupplementaryGridChargeGuard().Evaluate(missing, now));
        var zero = SelectedCurrentSiteTelemetryProvider.WithSupplementaryTelemetry(telemetry, snapshot with { BatteryPowerWatts = 0 }, true);
        Assert.Null(new SupplementaryGridChargeGuard().Evaluate(zero, now));
    }
}
