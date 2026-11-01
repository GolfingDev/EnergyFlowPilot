using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvTripSocPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    // 81 kWh usable, 18 kWh/100 km, minimum 15 %, margin 5 %, 11 kW (9.9 kW into the battery).
    private static readonly EvCarParameters Car = new(81, 18, 15, 5, 11000);

    [Fact]
    public void SingleTripNeedsRoundTripEnergyWithMarginPlusMinimum()
    {
        var departure = Now.AddHours(24);
        var trip = Trip(1, departure, 100, returnAt: departure.AddHours(8));

        var plan = Plan(30, trip);

        var requirement = Assert.Single(plan.Trips);
        Assert.Equal(37.8, requirement.EnergyKwh, 3); // 2 x 100 km x 0.18 kWh/km x 1.05
        Assert.Equal((37.8 + 12.15) / 81 * 100, requirement.RequiredSocPercent, 3);
        Assert.Equal(15, requirement.ExpectedSocAtReturnPercent, 3);
        Assert.True(requirement.IsReachable);
        // (49.95 - 24.3) kWh at 9.9 kW = 2.59 h before departure.
        Assert.Equal(departure.AddHours(-(49.95 - 24.3) / 9.9), requirement.LatestChargeStartUtc!.Value);
        Assert.Equal(30, plan.Course[0].SocPercent, 3);
        Assert.Equal(requirement.RequiredSocPercent, plan.Course.Single(p => p.AtUtc == departure).SocPercent, 3);
    }

    [Fact]
    public void ShortGapBeforeALongTripRaisesTheEarlierRequirement()
    {
        var first = Now.AddHours(5);
        var tripA = Trip(1, first, 20, returnAt: first.AddHours(10));
        var tripB = Trip(2, first.AddHours(11), 150);

        var plan = Plan(90, tripA, tripB);

        // B needs 56.7 + 12.15 kWh; one hour at home adds 9.9 kWh, so A must come back with 58.95 kWh.
        Assert.Equal((56.7 + 12.15) / 81 * 100, plan.Trips[1].RequiredSocPercent, 3);
        Assert.Equal((7.56 + 58.95) / 81 * 100, plan.Trips[0].RequiredSocPercent, 3);
        Assert.All(plan.Trips, trip => Assert.True(trip.IsReachable));
    }

    [Fact]
    public void TooLittleTimeIsReportedWithTheReachableLevel()
    {
        var plan = Plan(20, Trip(1, Now.AddHours(1), 100, returnAt: Now.AddHours(9)));

        var requirement = Assert.Single(plan.Trips);
        Assert.False(requirement.IsReachable);
        Assert.Contains("nur 32 %", requirement.Note); // 16.2 + 9.9 kWh = 32 % of 81 kWh
    }

    [Fact]
    public void TripLongerThanAFullBatteryIsFlagged()
    {
        var plan = Plan(100, Trip(1, Now.AddDays(1), 250, returnAt: Now.AddDays(1).AddHours(10)));

        var requirement = Assert.Single(plan.Trips);
        Assert.False(requirement.IsReachable);
        Assert.Contains("mehr als ein voller Akku", requirement.Note);
    }

    [Fact]
    public void MissingReturnIsAssumedAtEightInTheEveningAndUnknownSocIsFlagged()
    {
        var departure = new DateTimeOffset(2026, 11, 3, 6, 30, 0, TimeSpan.Zero); // 07:30 Berlin
        var plan = EvTripSocPlanner.Plan(new EvTripPlanInput(Now, null, [Trip(1, departure, 50)], Car, Berlin));

        var requirement = Assert.Single(plan.Trips);
        Assert.True(requirement.ReturnIsAssumed);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 19, 0, 0, TimeSpan.Zero), requirement.ReturnAtUtc); // 20:00 Berlin
        Assert.True(plan.CurrentSocIsAssumed);
        Assert.Contains(plan.Warnings, warning => warning.Contains("unbekannt"));
    }

    [Fact]
    public void ColdWeatherAddsTheWinterSurcharge()
    {
        var departure = Now.AddHours(24);
        var trip = Trip(1, departure, 100, returnAt: departure.AddHours(2));
        var temperatures = Enumerable.Range(0, 30).Select(hour => new HourlyTemperature(Now.AddHours(hour), 0m)).ToArray();

        var plan = EvTripSocPlanner.Plan(new EvTripPlanInput(Now, 50, [trip], Car, Berlin) { Temperatures = temperatures, WinterSurchargePerKelvinPercent = 1.2 });

        var requirement = Assert.Single(plan.Trips);
        Assert.Equal(18, requirement.WinterSurchargePercent, 3); // 15 K below 15 degrees x 1.2 %
        Assert.Equal(37.8 * 1.18, requirement.EnergyKwh, 3);
        Assert.Equal(0, requirement.TemperatureCelsius);
    }

    [Fact]
    public void MissingTemperaturesMeanNoSurchargeAndAHint()
    {
        var plan = EvTripSocPlanner.Plan(new EvTripPlanInput(Now, 50, [Trip(1, Now.AddHours(24), 100)], Car, Berlin) { WinterSurchargePerKelvinPercent = 1.2 });

        Assert.Equal(0, Assert.Single(plan.Trips).WinterSurchargePercent);
        Assert.Contains(plan.Warnings, warning => warning.Contains("Temperaturprognose"));
    }

    [Fact]
    public void ALaterImpossibleTripDoesNotInflateEarlierOnes()
    {
        // Short trip, then right away a trip that needs more than a full battery (e.g. a wrongly found far destination).
        var first = Now.AddHours(20);
        var shortTrip = Trip(1, first, 10, returnAt: first.AddHours(8));
        var longTrip = Trip(2, first.AddHours(8), 264, returnAt: first.AddHours(14));

        var plan = Plan(50, shortTrip, longTrip);

        // At most: own trip + a full battery for the next one would be needed, never 187 %.
        Assert.True(plan.Trips[0].RequiredSocPercent <= 100 + 2 * 10 * 0.18 * 1.05 / 81 * 100 + 0.01);
        Assert.Contains("Nachladen", plan.Trips[0].Note);
        Assert.Contains("mehr als ein voller Akku", plan.Trips[1].Note);
    }

    [Fact]
    public void PastTripsAreIgnored()
    {
        var plan = Plan(50, Trip(1, Now.AddHours(-2), 100));

        Assert.Empty(plan.Trips);
        Assert.Single(plan.Course);
    }

    private static EvTripPlan Plan(double soc, params EvTrip[] trips) =>
        EvTripSocPlanner.Plan(new EvTripPlanInput(Now, soc, trips, Car, Berlin));

    private static EvTrip Trip(int id, DateTimeOffset departure, double distanceKm, DateTimeOffset? returnAt = null) =>
        new(id, departure, returnAt, $"Ziel {id}", $"Adresse {id}", distanceKm, distanceKm, Now);
}
