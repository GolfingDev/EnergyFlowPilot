using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointBatterySlotCommitmentTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 7, 0, 0, TimeSpan.Zero);

    private static JointBatterySlot Slot(int victron, int e3dc) => new(new ForecastTimeSlot(Start, Start.AddMinutes(15)), 0.18m, "EUR", false,
        victron + e3dc > 0 ? BatteryChargeSource.Grid : null, [new("victron", victron, 10, 20), new("e3dc", e3dc, 11, 12)], "Test", false);

    private static JointBatteryPlan Plan(JointBatterySlot slot) => new(Start, [], [slot], 0, []);

    [Fact]
    public void CommittedDecisionHoldsForItsWholeQuarterHourOnly()
    {
        var committed = Slot(3000, 500);
        Assert.Same(committed, JointBatterySlotCommitment.Active(committed, Start));
        Assert.Same(committed, JointBatterySlotCommitment.Active(committed, Start.AddMinutes(14).AddSeconds(59)));
        Assert.Null(JointBatterySlotCommitment.Active(committed, Start.AddMinutes(15)));
        Assert.Null(JointBatterySlotCommitment.Active(null, Start));
    }

    [Fact]
    public void ChangedPlanForTheSameQuarterHourIsDetectedButSmallNoiseIsNot()
    {
        var committed = Slot(3000, 500);
        // The situation reported on 24.10.2026: a newer plan drops a started charge.
        Assert.True(JointBatterySlotCommitment.PlanDeviates(committed, Plan(Slot(0, 0))));
        Assert.False(JointBatterySlotCommitment.PlanDeviates(committed, Plan(Slot(2950, 520))));
        Assert.False(JointBatterySlotCommitment.PlanDeviates(committed, null));
    }
}
