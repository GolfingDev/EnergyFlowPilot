using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvChargePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvCarParameters Car = new(81, 18, 15, 5, 11000);

    [Fact]
    public void TripEnergyComesFromTheCheapestQuarterHoursBeforeDeparture()
    {
        // Cheap at slots 56-59, departure at slot 72, an even cheaper slot while the car is away.
        var slots = Slots(96, index => index is >= 56 and < 60 ? 0.10 : index >= 72 ? 0.01 : 0.30);
        var departure = Now.AddHours(18);
        var plan = EvChargePlanner.Plan(Input(slots, [Requirement(departure, energyKwh: 20)], currentSoc: 30) with { BaseSocPercent = 0 }
            with { FutureValuePerKwh = 0 });

        // Before leaving: 15 % reserve (12.15 kWh) + 20 kWh trip - 24.3 kWh = 7.85 kWh in the battery = 8.7 kWh at the wallbox.
        // The wallbox's fixed current steps may add a little more than needed.
        Assert.InRange(plan.GridKwh, 8.7, 9.8);
        Assert.All(plan.Slots, slot => Assert.True(slot.EndsAtUtc <= departure));
        Assert.All(plan.Slots, slot => Assert.Equal(0.10, slot.PricePerKwh));
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.Trip, slot.Reason));
        Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("fehlen"));
    }

    [Fact]
    public void WithoutTripTheCarChargesOnlyWhenCheaperThanLater()
    {
        var slots = Slots(16, index => index < 8 ? 0.05 : 0.30);

        var plan = EvChargePlanner.Plan(Input(slots, [], currentSoc: 70, carTarget: 80) with { BaseSocPercent = 0, FutureValuePerKwh = 0.20 });

        // 70 % -> 80 % = 8.1 kWh in the battery = 9 kWh at the wallbox, only in the cheap quarter hours.
        Assert.Equal(9, plan.GridKwh, 1);
        Assert.All(plan.Slots, slot => Assert.Equal(0.05, slot.PricePerKwh));
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.CheapPrice, slot.Reason));
    }

    [Fact]
    public void ExpensiveHoursStayUnusedWithoutNeed()
    {
        var plan = EvChargePlanner.Plan(Input(Slots(16, _ => 0.30), [], currentSoc: 60) with { BaseSocPercent = 0, FutureValuePerKwh = 0.20 });

        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void BaseStateOfChargeIsReachedEvenWhenNotCheap()
    {
        // A full day of prices: by price, the base state of charge has to be reached within it.
        var plan = EvChargePlanner.Plan(Input(Slots(96, index => index < 8 ? 0.35 : 0.30), [], currentSoc: 40) with { BaseSocPercent = 50, FutureValuePerKwh = 0.10 });

        // 40 % -> 50 % = 8.1 kWh in the battery = 9 kWh at the wallbox, in the cheaper second half.
        // The 1 kWh state grid may leave a few watt hours short of exactly 50 %.
        Assert.InRange(plan.GridKwh, 8.9, 9.5);
        Assert.All(plan.Slots, slot => Assert.Equal(0.30, slot.PricePerKwh));
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.BaseSoc, slot.Reason));
    }

    [Fact]
    public void ChargeNowStartsAtOnceBelowTheBaseStateOfCharge()
    {
        // Operator 2026-11-03: the driver answered "charge now"; cheaper hours later do not count below the base.
        var plan = EvChargePlanner.Plan(Input(Slots(96, index => index < 8 ? 0.35 : 0.30), [], currentSoc: 40) with
            { BaseSocPercent = 50, FutureValuePerKwh = 0.10, BaseMode = EvBaseChargeMode.Immediate });

        // 40 % -> 50 % = 8.1 kWh in the battery = 9 kWh at the wallbox, right now at full power.
        // The 1 kWh state grid may leave a few watt hours short of exactly 50 %.
        Assert.InRange(plan.GridKwh, 8.9, 9.5);
        Assert.Equal(Now, plan.Slots[0].StartsAtUtc);
        Assert.All(plan.Slots, slot => Assert.Equal(0.35, slot.PricePerKwh));
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.BaseSoc, slot.Reason));
    }

    [Theory]
    [InlineData(0.05, true)]   // home battery values the PV less than grid power costs: the car takes it
    [InlineData(0.40, false)]  // home battery saves more with it: the car charges from the grid instead
    public void PvGoesWhereItSavesMore(double batteryValue, bool carTakesPv)
    {
        var slots = Slots(4, _ => 0.30).Select(slot => slot with { BatteryPvKwh = 2.5, BatteryPvValuePerKwh = batteryValue }).ToList();

        var plan = EvChargePlanner.Plan(Input(slots, [Requirement(Now.AddHours(1), energyKwh: 4)], currentSoc: 15) with
        {
            BaseSocPercent = 0, FutureValuePerKwh = 0, PvConfidence = 1
        });

        Assert.Equal(carTakesPv, plan.PvKwh > 1);
        Assert.Equal(!carTakesPv, plan.GridKwh > 1);
    }

    [Fact]
    public void NoChargingWhileTheCarIsAway()
    {
        var slots = Slots(96, index => index is >= 8 and < 16 ? 0.01 : 0.20);
        // Away from 13:00 to 17:00 UTC exactly over the cheapest quarter hours.
        var trip = Requirement(Now.AddHours(1), energyKwh: 8.1, returnAt: Now.AddHours(5));

        var plan = EvChargePlanner.Plan(Input(slots, [trip], currentSoc: 40) with { BaseSocPercent = 50, FutureValuePerKwh = 0 });

        Assert.All(plan.Slots, slot => Assert.True(slot.EndsAtUtc <= Now.AddHours(1) || slot.StartsAtUtc >= Now.AddHours(5)));
        Assert.NotEmpty(plan.Slots);
    }

    [Fact]
    public void SmallDeficitsAreChargedAtLeastAtTheMinimumCurrent()
    {
        var plan = EvChargePlanner.Plan(Input(Slots(4, _ => 0.20), [Requirement(Now.AddHours(1), energyKwh: 12.6)], currentSoc: 30) with
        {
            BaseSocPercent = 0, FutureValuePerKwh = 0
        });

        Assert.All(plan.Slots, slot => Assert.True(slot.PowerWatts >= EvChargePlanner.MinimumChargePowerWatts - 1));
        Assert.NotEmpty(plan.Slots);
    }

    [Fact]
    public void UnknownStateOfChargePlansOnlyTrips()
    {
        var slots = Slots(96, index => index < 8 ? 0.05 : 0.30);
        var input = new EvChargePlanInput(Now, null, null, Car, 50, 0.08, slots, []) { FutureValuePerKwh = 0.20 };

        var plan = EvChargePlanner.Plan(input);

        Assert.Empty(plan.Slots);
        Assert.Contains(plan.Warnings, warning => warning.Contains("nur Fahrten"));
    }

    [Fact]
    public void BaseStateOfChargeWaitsForPricesThatAreNotPublishedYet()
    {
        // Only four hours of prices known: by price, the base state of charge can still be reached tomorrow night.
        var plan = EvChargePlanner.Plan(Input(Slots(16, _ => 0.30), [], currentSoc: 40) with { BaseSocPercent = 50, FutureValuePerKwh = 0.10 });
        Assert.Empty(plan.Slots);

        // "Charge now" does not wait.
        var now = EvChargePlanner.Plan(Input(Slots(16, _ => 0.30), [], currentSoc: 40) with
            { BaseSocPercent = 50, FutureValuePerKwh = 0.10, BaseMode = EvBaseChargeMode.Immediate });
        Assert.Equal(Now, now.Slots[0].StartsAtUtc);
    }

    [Fact]
    public void BaseChoiceIsAskedOnceBelowTheBaseAndDefaultsToPrice()
    {
        var session = new EvChargingSession(7, "cp", 1, "tag", Now, 0, null, null, null, null, null);

        var fresh = EvBaseChargeChoice.Resolve(session, 26, 30);
        Assert.True(fresh.ShouldAsk);
        Assert.Equal(EvBaseChargeMode.PriceDriven, fresh.Mode);

        var asked = EvBaseChargeChoice.Resolve(session with { BaseChoiceAskedAtUtc = Now }, 26, 30);
        Assert.False(asked.ShouldAsk);
        Assert.Equal(Now.AddMinutes(15), asked.DefaultAtUtc);

        var answered = EvBaseChargeChoice.Resolve(session with { BaseChoiceAskedAtUtc = Now, BaseChargeMode = EvBaseChargeMode.Immediate, BaseChoiceBy = "Torsten" }, 26, 30);
        Assert.Equal(EvBaseChargeMode.Immediate, answered.Mode);
        Assert.Equal("Torsten", answered.DecidedBy);

        Assert.False(EvBaseChargeChoice.Resolve(session, 45, 30).ShouldAsk);
        Assert.False(EvBaseChargeChoice.Resolve(session with { StoppedAtUtc = Now }, 26, 30).ShouldAsk);

        // Stopped by hand counts only while the session runs; plugging in again starts a new session.
        var stopped = session with { IsStoppedByUser = true, StoppedBy = "Carla" };
        Assert.True(EvBaseChargeChoice.Resolve(stopped, 26, 30).IsStopped);
        Assert.Equal("Carla", EvBaseChargeChoice.Resolve(stopped, 26, 30).StoppedBy);
        Assert.False(EvBaseChargeChoice.Resolve(stopped with { StoppedAtUtc = Now }, 26, 30).IsStopped);
    }

    [Fact]
    public void UnreachableDeficitIsReported()
    {
        var plan = EvChargePlanner.Plan(Input(Slots(2, _ => 0.2), [Requirement(Now.AddMinutes(30), energyKwh: 60)], currentSoc: 20));

        Assert.Contains(plan.Warnings, warning => warning.Contains("fehlen"));
    }

    [Fact]
    public void ScenariosShowGridCostForLessAndMoreSun()
    {
        var slots = Slots(8, _ => 0.30).Select(slot => slot with { SurplusKwh = 1.5 }).ToList();

        var plan = EvChargePlanner.Plan(Input(slots, [Requirement(Now.AddHours(2), energyKwh: 15)], currentSoc: 15) with { BaseSocPercent = 0, FutureValuePerKwh = 0 });

        Assert.Equal(3, plan.Scenarios.Count);
        Assert.True(plan.Scenarios[0].GridKwh >= plan.Scenarios[1].GridKwh);
        Assert.True(plan.Scenarios[1].GridKwh >= plan.Scenarios[2].GridKwh);
    }

    [Fact]
    public void WithoutSlotsThePlanSaysSo()
    {
        var plan = EvChargePlanner.Plan(Input([], [], currentSoc: 50));

        Assert.False(plan.HasInputSlots);
        Assert.Contains(plan.Warnings, warning => warning.Contains("Keine Preise"));
    }

    private static EvChargePlanInput Input(IReadOnlyList<EvChargeSlotInput> slots, IReadOnlyList<EvTripRequirement> trips, double currentSoc,
        double? carTarget = null) =>
        new(Now, currentSoc, carTarget, Car, 0, 0.08, slots, trips);

    private static List<EvChargeSlotInput> Slots(int quarterHours, Func<int, double> price) =>
        Enumerable.Range(0, quarterHours)
            .Select(index => new EvChargeSlotInput(Now.AddMinutes(15 * index), Now.AddMinutes(15 * (index + 1)), price(index), false, 0))
            .ToList();

    private static EvTripRequirement Requirement(DateTimeOffset departure, double energyKwh, DateTimeOffset? returnAt = null)
    {
        var back = returnAt ?? departure.AddHours(8);
        var trip = new EvTrip(1, departure, back, "Ziel", "Adresse", 50, 40, Now);
        var required = 15 + energyKwh / 81 * 100;
        return new EvTripRequirement(trip, back, false, energyKwh, required, 15, null, true, null);
    }
}
