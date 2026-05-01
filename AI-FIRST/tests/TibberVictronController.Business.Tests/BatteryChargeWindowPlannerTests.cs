using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class BatteryChargeWindowPlannerTests
{
    private static readonly DateTimeOffset WindowStartsAtUtc = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEndsAtUtc = new(2026, 5, 1, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlanCheapestSlotsForFullChargeUsesWholeWindowWhenBatteryNeedsWholeWindow()
    {
        var planner = new BatteryChargeWindowPlanner();
        var priceForecast = CreateFlatPriceForecast();
        var batteryState = new BatteryState(0m, WindowStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var plan = planner.PlanCheapestSlotsForFullCharge(priceForecast, batteryState, batteryConfiguration);

        Assert.Equal(TimeSpan.FromHours(4), plan.RequiredChargeDuration);
        Assert.Equal(12m, plan.RequiredEnergyKwh);
        Assert.Equal(16, plan.PlannedChargeSlots.Count);
        Assert.Equal(priceForecast.Select(priceSlot => priceSlot.TimeSlot), plan.PlannedChargeSlots);
    }

    [Fact]
    public void PlanCheapestSlotsForFullChargeChoosesCheapestSlotsWhenBatteryNeedsOnlyOneHour()
    {
        var planner = new BatteryChargeWindowPlanner();
        var priceForecast = CreatePriceForecastWithCheapestMiddleHour();
        var batteryState = new BatteryState(75m, WindowStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var plan = planner.PlanCheapestSlotsForFullCharge(priceForecast, batteryState, batteryConfiguration);

        Assert.Equal(TimeSpan.FromHours(1), plan.RequiredChargeDuration);
        Assert.Equal(3m, plan.RequiredEnergyKwh);
        Assert.Equal(
            CreateExpectedSlots(WindowStartsAtUtc.AddHours(1), WindowStartsAtUtc.AddHours(2)),
            plan.PlannedChargeSlots);
    }

    [Fact]
    public void PlanCheapestSlotsForFullChargeChoosesLowestIndividualSlotsWhenPricesAreNotContiguous()
    {
        var planner = new BatteryChargeWindowPlanner();
        var priceForecast = CreatePriceForecastWithScatteredCheapSlots();
        var batteryState = new BatteryState(75m, WindowStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var plan = planner.PlanCheapestSlotsForFullCharge(priceForecast, batteryState, batteryConfiguration);

        Assert.Equal(
            new[]
            {
                new ForecastTimeSlot(WindowStartsAtUtc.AddMinutes(30), WindowStartsAtUtc.AddMinutes(45)),
                new ForecastTimeSlot(WindowStartsAtUtc.AddMinutes(75), WindowStartsAtUtc.AddMinutes(90)),
                new ForecastTimeSlot(WindowStartsAtUtc.AddMinutes(120), WindowStartsAtUtc.AddMinutes(135)),
                new ForecastTimeSlot(WindowStartsAtUtc.AddMinutes(195), WindowStartsAtUtc.AddMinutes(210))
            },
            plan.PlannedChargeSlots);
    }

    [Fact]
    public void PlanCheapestSlotsForFullChargeReturnsNoSlotsWhenBatteryIsAlreadyFull()
    {
        var planner = new BatteryChargeWindowPlanner();
        var priceForecast = CreateFlatPriceForecast();
        var batteryState = new BatteryState(100m, WindowStartsAtUtc);
        var batteryConfiguration = new BatteryConfiguration(12m, maximumChargePowerWatts: 3000);

        var plan = planner.PlanCheapestSlotsForFullCharge(priceForecast, batteryState, batteryConfiguration);

        Assert.Equal(TimeSpan.Zero, plan.RequiredChargeDuration);
        Assert.Equal(0m, plan.RequiredEnergyKwh);
        Assert.Empty(plan.PlannedChargeSlots);
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreateFlatPriceForecast()
    {
        return CreatePriceForecast(slotIndex => 0.20m);
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreatePriceForecastWithCheapestMiddleHour()
    {
        return CreatePriceForecast(slotIndex => slotIndex is >= 4 and <= 7 ? -0.20m : 0.20m);
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreatePriceForecastWithScatteredCheapSlots()
    {
        return CreatePriceForecast(slotIndex => slotIndex switch
        {
            2 => -0.40m,
            5 => -0.30m,
            8 => -0.20m,
            13 => -0.10m,
            _ => 0.20m
        });
    }

    private static IReadOnlyList<TibberPriceForecastSlot> CreatePriceForecast(Func<int, decimal> priceFactory)
    {
        var slots = new List<TibberPriceForecastSlot>();
        var currentSlotStartUtc = WindowStartsAtUtc;
        var slotIndex = 0;

        while (currentSlotStartUtc < WindowEndsAtUtc)
        {
            var timeSlot = new ForecastTimeSlot(currentSlotStartUtc, currentSlotStartUtc.AddMinutes(15));
            slots.Add(new TibberPriceForecastSlot(timeSlot, priceFactory(slotIndex), "EUR"));

            currentSlotStartUtc = currentSlotStartUtc.AddMinutes(15);
            slotIndex++;
        }

        return slots;
    }

    private static IReadOnlyList<ForecastTimeSlot> CreateExpectedSlots(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc)
    {
        return CreatePriceForecast(_ => 0m)
            .Select(priceSlot => priceSlot.TimeSlot)
            .Where(timeSlot => timeSlot.StartsAtUtc >= startsAtUtc && timeSlot.EndsAtUtc <= endsAtUtc)
            .ToArray();
    }
}
