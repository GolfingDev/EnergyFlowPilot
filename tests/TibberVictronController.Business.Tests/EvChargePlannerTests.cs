using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvChargePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly EvCarParameters Car = new(81, 18, 15, 5, 11000);

    [Fact]
    public void TripDeficitGoesIntoTheCheapestQuarterHoursBeforeDeparture()
    {
        // 12 hours of slots; cheap at 02:00-03:00 UTC, departure 06:00 UTC, an even cheaper slot after departure.
        var slots = Slots(96, index => index is >= 56 and < 60 ? 0.10 : index >= 72 ? 0.01 : 0.30);
        var departure = Now.AddHours(18);
        var trip = Requirement(departure, requiredSoc: 40, energyKwh: 20);

        var plan = EvChargePlanner.Plan(Input(slots, [trip], currentSoc: 30, baseSoc: 0));

        // (40 % - 30 %) x 81 kWh = 8.1 kWh into the battery = 9 kWh at the wallbox (plus rounding up to the minimum current).
        Assert.InRange(plan.GridKwh, 9, 9.3);
        Assert.All(plan.Slots, slot => Assert.True(slot.EndsAtUtc <= departure));
        Assert.All(plan.Slots, slot => Assert.Equal(0.10, slot.PricePerKwh));
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.Trip, slot.Reason));
    }

    [Fact]
    public void PvSurplusIsUsedBeforeGridAndAlwaysUpToTheCarLimit()
    {
        var slots = Slots(8, _ => 0.05).Select((slot, index) => index < 4 ? slot with { SurplusKwh = 2.0 } : slot).ToList();

        var plan = EvChargePlanner.Plan(Input(slots, [], currentSoc: 60, baseSoc: 0, carTarget: 80));

        Assert.Equal(8, plan.PvKwh, 3); // four quarter hours with 2 kWh (8 kW) surplus each
        Assert.Equal(0, plan.GridKwh, 3);
        Assert.All(plan.Slots, slot => Assert.Equal(EvChargeReason.PvSurplus, slot.Reason));
    }

    [Fact]
    public void SurplusBelowTheWallboxMinimumIsNotChargedAsPureSurplus()
    {
        var slots = Slots(4, _ => 0.30).Select(slot => slot with { SurplusKwh = 0.5 }).ToList(); // 2 kW < 4.14 kW

        var plan = EvChargePlanner.Plan(Input(slots, [], currentSoc: 60, baseSoc: 0));

        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void SmallDeficitsAreChargedAtLeastAtTheMinimumCurrent()
    {
        var slots = Slots(4, _ => 0.20);
        var trip = Requirement(Now.AddHours(1), requiredSoc: 30.5, energyKwh: 5);

        var plan = EvChargePlanner.Plan(Input(slots, [trip], currentSoc: 30, baseSoc: 0));

        var slot = Assert.Single(plan.Slots);
        Assert.Equal(EvChargePlanner.MinimumChargePowerWatts, slot.PowerWatts, 0);
    }

    [Fact]
    public void NoChargingWhileTheCarIsAwayAndBaseSocAfterwards()
    {
        var slots = Slots(48, index => index is >= 8 and < 16 ? 0.01 : 0.20);
        // Away from 13:00 to 17:00 UTC exactly over the cheapest quarter hours.
        var trip = Requirement(Now.AddHours(1), requiredSoc: 0, energyKwh: 8.1, returnAt: Now.AddHours(5));

        var plan = EvChargePlanner.Plan(Input(slots, [trip], currentSoc: 40, baseSoc: 50));

        Assert.All(plan.Slots, slot => Assert.True(slot.EndsAtUtc <= Now.AddHours(1) || slot.StartsAtUtc >= Now.AddHours(5)));
        // Back with 30 %; base 50 % needs 16.2 kWh in the battery = 18 kWh at the wallbox.
        Assert.Equal(18, plan.GridKwh, 1);
        Assert.Contains(plan.Slots, slot => slot.Reason == EvChargeReason.BaseSoc);
    }

    [Fact]
    public void UnreachableDeficitIsReported()
    {
        var trip = Requirement(Now.AddMinutes(30), requiredSoc: 90, energyKwh: 30);

        var plan = EvChargePlanner.Plan(Input(Slots(2, _ => 0.2), [trip], currentSoc: 20, baseSoc: 0));

        Assert.Contains(plan.Warnings, warning => warning.Contains("fehlen"));
    }

    private static EvChargePlanInput Input(IReadOnlyList<EvChargeSlotInput> slots, IReadOnlyList<EvTripRequirement> trips, double currentSoc,
        double baseSoc, double? carTarget = null) =>
        new(Now, currentSoc, carTarget, Car, baseSoc, 0.08, slots, trips);

    private static List<EvChargeSlotInput> Slots(int quarterHours, Func<int, double> price) =>
        Enumerable.Range(0, quarterHours)
            .Select(index => new EvChargeSlotInput(Now.AddMinutes(15 * index), Now.AddMinutes(15 * (index + 1)), price(index), false, 0))
            .ToList();

    private static EvTripRequirement Requirement(DateTimeOffset departure, double requiredSoc, double energyKwh, DateTimeOffset? returnAt = null)
    {
        var back = returnAt ?? departure.AddHours(8);
        var trip = new EvTrip(1, departure, back, "Ziel", "Adresse", 50, 40, Now);
        return new EvTripRequirement(trip, back, false, energyKwh, requiredSoc, requiredSoc - energyKwh / 81 * 100, null, true, null);
    }
}
