using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointManualChargeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 21, 0, 0, TimeSpan.Zero);

    // Real site sizes: Victron 19.2 kWh, E3/DC 14.01 kWh with its currently reported 97 W effective limit.
    private static JointBatteryUnit Victron(decimal soc) => new("victron", new BatteryConfiguration(new BatteryConfigurationValues {
        TotalCapacityKwh = 19.2m, MinimumStateOfChargePercent = 10, PlanningMinimumStateOfChargePercent = 10,
        PlanningMaximumStateOfChargePercent = 95, MaximumChargePowerWatts = 5000, MaximumDischargePowerWatts = 5000 }), new(soc, Now), 0);
    private static JointBatteryUnit E3dc(decimal soc, int? limit = 3000) => new("e3dc", new BatteryConfiguration(new BatteryConfigurationValues {
        TotalCapacityKwh = 14.01m, MinimumStateOfChargePercent = 10.8m, PlanningMinimumStateOfChargePercent = 10.8m,
        PlanningMaximumStateOfChargePercent = 95, MaximumChargePowerWatts = 3000, MaximumDischargePowerWatts = 3000 }), new(soc, Now), 0, limit, limit);
    private static JointManualChargeRequest Request(decimal target = 2, int maxWatts = 0, decimal? baseline = null) =>
        new(target, maxWatts, Now.AddHours(6), baseline);

    [Fact]
    public void ChargesBothBatteriesAndSplitsByHeadroomAndLimits()
    {
        var step = JointManualCharge.Evaluate(Request(), [Victron(11), E3dc(11.2m, 97)], Now, Now.AddSeconds(15));
        Assert.Equal(JointManualChargeOutcome.Charging, step.Outcome);
        Assert.NotNull(step.Command);
        Assert.Equal(BatteryChargeSource.Grid, step.Command.ChargeSource);
        Assert.Equal(5000, step.Command.VictronWatts);
        // The E3/DC's reported effective limit is respected instead of its 3 kW nameplate.
        Assert.Equal(97, step.Command.E3dcWatts);
    }

    [Fact]
    public void OptionalPowerLimitCapsTheSumOfBothBatteries()
    {
        var step = JointManualCharge.Evaluate(Request(maxWatts: 2000), [Victron(11), E3dc(11.2m)], Now, Now.AddSeconds(15));
        Assert.Equal(2000, step.Command!.VictronWatts + step.Command.E3dcWatts);
        Assert.True(step.Command.VictronWatts > 0 && step.Command.E3dcWatts > 0);
    }

    [Fact]
    public void StopsWhenTheStoredEnergyGrewByTheTarget()
    {
        var baseline = JointManualCharge.StoredEnergyKwh([Victron(11), E3dc(11.2m)]);
        // +2 kWh in the Victron alone: 2 / 19.2 kWh = 10.42 %.
        var step = JointManualCharge.Evaluate(Request(baseline: baseline), [Victron(21.42m), E3dc(11.2m)], Now, Now.AddSeconds(15));
        Assert.Equal(JointManualChargeOutcome.TargetReached, step.Outcome);
        Assert.Null(step.Command);
        Assert.InRange(step.ChargedKwh, 2m, 2.01m);
    }

    [Fact]
    public void StopsWhenBothBatteriesReachTheirPlanningMaximum()
    {
        var full = new[] { Victron(95), E3dc(96) };
        var step = JointManualCharge.Evaluate(Request(baseline: JointManualCharge.StoredEnergyKwh(full)), full, Now, Now.AddSeconds(15));
        Assert.Equal(JointManualChargeOutcome.BatteriesFull, step.Outcome);
        Assert.Null(step.Command);
    }

    [Fact]
    public void OneFullBatteryDoesNotStopTheOther()
    {
        var step = JointManualCharge.Evaluate(Request(baseline: 30), [Victron(95), E3dc(50)], Now, Now.AddSeconds(15));
        Assert.Equal(JointManualChargeOutcome.Charging, step.Outcome);
        Assert.Equal(0, step.Command!.VictronWatts);
        Assert.True(step.Command.E3dcWatts > 0);
    }

    [Fact]
    public void SafetyDeadlineEndsTheCharge()
    {
        var step = JointManualCharge.Evaluate(Request() with { ExpiresAtUtc = Now }, [Victron(11), E3dc(11.2m)], Now, Now.AddSeconds(15));
        Assert.Equal(JointManualChargeOutcome.Expired, step.Outcome);
        Assert.Null(step.Command);
    }
}
