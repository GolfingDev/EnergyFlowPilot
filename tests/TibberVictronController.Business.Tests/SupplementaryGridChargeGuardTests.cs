using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class SupplementaryGridChargeGuardTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T12:47:00Z");
    private static CurrentSiteTelemetry Reading(DateTimeOffset time, int discharge = 0) => new(27, 3090, time, 3030)
    { SupplementaryBatteryMonitoringRequired = true, SupplementaryBatteryMeasuredAtUtc = time, SupplementaryBatteryDischargeWatts = discharge };

    [Fact]
    public void ScreenshotScenarioIsBlockedAndFiveMinuteCooldownRequiresFreshCleanData()
    {
        var guard = new SupplementaryGridChargeGuard();
        Assert.Equal(CurrentBatteryDecisionRuleIds.SupplementaryGridChargeBlocked, guard.Evaluate(Reading(Now, 774), Now)?.RuleName);
        Assert.Equal(CurrentBatteryDecisionRuleIds.SupplementaryGridChargeCooldown, guard.Evaluate(Reading(Now.AddMinutes(4)), Now.AddMinutes(4))?.RuleName);
        Assert.Null(guard.Evaluate(Reading(Now.AddMinutes(5)), Now.AddMinutes(5)));
    }

    [Fact]
    public void EveryNewDischargeRestartsCooldownRatherThanOscillating()
    {
        var guard = new SupplementaryGridChargeGuard();
        guard.Evaluate(Reading(Now, 774), Now);
        guard.Evaluate(Reading(Now.AddMinutes(4), 100), Now.AddMinutes(4));
        Assert.NotNull(guard.Evaluate(Reading(Now.AddMinutes(8)), Now.AddMinutes(8)));
        Assert.Null(guard.Evaluate(Reading(Now.AddMinutes(9)), Now.AddMinutes(9)));
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(1)]
    public void OldAndFutureReadingsFailClosed(int minutes)
    {
        var guard = new SupplementaryGridChargeGuard();
        Assert.Equal(CurrentBatteryDecisionRuleIds.SupplementaryTelemetryMissing, guard.Evaluate(Reading(Now.AddMinutes(minutes)), Now)?.RuleName);
    }

    [Fact]
    public void ConfiguredButUnknownSecondaryPowerIsNotTreatedAsZero()
    {
        var guard = new SupplementaryGridChargeGuard();
        Assert.Equal(CurrentBatteryDecisionRuleIds.SupplementaryTelemetryMissing,
            guard.Evaluate(new CurrentSiteTelemetry(27, 3090, Now) { SupplementaryBatteryMonitoringRequired = true }, Now)?.RuleName);
        Assert.NotNull(guard.Evaluate(Reading(Now.AddMinutes(1)), Now.AddMinutes(1)));
    }

    [Fact]
    public void InstallationWithoutSecondaryBatteryAndMeasuredChargingAreAllowed()
    {
        Assert.Null(new SupplementaryGridChargeGuard().Evaluate(new CurrentSiteTelemetry(1000, 0, Now), Now));
        Assert.Null(new SupplementaryGridChargeGuard().Evaluate(Reading(Now), Now));
        Assert.Null(new SupplementaryGridChargeGuard().Evaluate(Reading(Now, 50), Now));
    }
}
