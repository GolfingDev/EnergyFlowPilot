using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class JointConsumptionProfileWallboxTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WallboxPowerIsNotLearnedAsHouseDemand()
    {
        // 11.8 kW from the grid, both batteries idle, no PV: 11 kW of it is the car.
        var sample = new LiveConsumptionSample(11800, At, 11800, 0, 50, 0)
        {
            SupplementaryBatteryPowerWatts = 0,
            SupplementaryPvProductionWatts = 0,
            EvChargingWatts = 11000
        };

        Assert.Equal(800, JointConsumptionProfile.GrossWatts(sample));
    }

    [Fact]
    public void SamplesWithoutWallboxDataStayUnchanged()
    {
        var sample = new LiveConsumptionSample(900, At, 900, 0, 50, 0) { SupplementaryBatteryPowerWatts = 0, SupplementaryPvProductionWatts = 0 };

        Assert.Equal(900, JointConsumptionProfile.GrossWatts(sample));
    }

    private static LiveConsumptionSample Sample(DateTimeOffset at, decimal gridWatts, decimal? evWatts) =>
        new(gridWatts, at, gridWatts, 0, 50, 0) { SupplementaryBatteryPowerWatts = 0, SupplementaryPvProductionWatts = 0, EvChargingWatts = evWatts };

    [Fact]
    public void HistoryWithoutAnyWallboxValueStaysFullyUsable()
    {
        // A site without a wallbox, or samples from before the column existed: null is the only value there is.
        var samples = Enumerable.Range(0, 10).Select(i => Sample(At.AddMinutes(i), 900, null)).ToArray();

        Assert.Equal(10, JointConsumptionProfile.WithoutUnknownWallboxPower(samples).Count);
    }

    [Fact]
    public void UnknownWallboxPowerIsSkippedOnlyAfterTheWallboxStartedReporting()
    {
        var samples = new[]
        {
            Sample(At, 900, null),                    // before the wallbox was connected: usable
            Sample(At.AddMinutes(1), 900, 0),         // wallbox reports, no car charging
            Sample(At.AddMinutes(2), 11900, null),    // wallbox offline while a car may charge: unknown
            Sample(At.AddMinutes(3), 11900, 11000),   // measured charge
            Sample(At.AddMinutes(4), 900, 0)
        };

        var usable = JointConsumptionProfile.WithoutUnknownWallboxPower(samples);

        Assert.Equal(4, usable.Count);
        Assert.DoesNotContain(usable, s => s.MeasuredAtUtc == At.AddMinutes(2));
    }

    [Fact]
    public void ChargeDuringAWallboxGapIsNotLearnedAsHouseDemand()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        // Two earlier days with 900 W in the 21:00 quarter hour (local), wallbox reporting 0.
        var samples = new List<LiveConsumptionSample>();
        foreach (var day in new[] { -3, -2 })
            for (var second = 0; second < 900; second += 30)
                samples.Add(Sample(At.AddDays(day).AddSeconds(second), 900, 0));
        // Yesterday the wallbox was offline for that quarter hour while the car drew 11 kW.
        for (var second = 0; second < 900; second += 30)
            samples.Add(Sample(At.AddDays(-1).AddSeconds(second), 11900, null));

        var model = JointConsumptionProfile.Learn(samples, zone, At);

        var local = TimeZoneInfo.ConvertTime(At, zone);
        var kwh = model.GetSlotConsumptionKwh(DateOnly.FromDateTime(local.DateTime), local.Hour * 4 + local.Minute / 15, TimeSpan.FromMinutes(15));
        Assert.NotNull(kwh);
        Assert.Equal(0.225m, Math.Round(kwh!.Value, 3));
    }

    [Theory]
    [InlineData(1800, 2000)]   // charging 1800 W DC draws 2000 W AC at 90 % single-direction efficiency
    [InlineData(-2000, -1800)] // discharging 2000 W DC delivers 1800 W AC
    [InlineData(0, 0)]
    public void BatteryPowerIsConvertedToTheAcSideLikeTheLiveLimiterDoes(int dcWatts, int expectedAcWatts) =>
        Assert.Equal(expectedAcWatts, Math.Round(BatteryAcPower.FromDcSide(dcWatts, 81m), 6));

    [Fact]
    public void UnusableEfficiencyLeavesTheMeasurementUnchanged() =>
        Assert.Equal(1500, BatteryAcPower.FromDcSide(1500, 0));

    [Fact]
    public void ConversionLossesOfBothBatteriesAreNotLearnedAsHouseDemand()
    {
        // House 1000 W. Victron charges 1800 W DC (2000 W AC), E3/DC discharges 2000 W DC (1800 W AC):
        // grid = 1000 + 2000 - 1800 = 1200 W. Without conversion the profile would learn 1400 W.
        var sample = new LiveConsumptionSample(0, At, 1200, 1800, 50, null) { SupplementaryBatteryPowerWatts = -2000, SupplementaryPvProductionWatts = 0 };

        Assert.Equal(1400, JointConsumptionProfile.GrossWatts(sample));
        Assert.Equal(1000, Math.Round(JointConsumptionProfile.GrossWatts(sample, new JointProfileEfficiencies(81, 81))!.Value, 6));
    }
}
