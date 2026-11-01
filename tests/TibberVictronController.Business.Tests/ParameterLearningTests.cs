using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class ParameterLearningTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PvForecastTooHighGivesAFactorBelowOneAfterAWeek()
    {
        var (forecasts, samples) = PvDays(days: 8, forecastKwh: 1.0, measuredKwh: 0.8);

        var correction = PvForecastCorrectionLearner.Learn(forecasts, samples, Berlin, Now);

        Assert.True(correction.IsReady);
        Assert.Equal(8, correction.DaysWithData);
        Assert.Equal(0.8, correction.OverallFactor, 3);
        Assert.Equal(0.8, correction.FactorFor(forecasts[0].SlotStartUtc, Berlin), 3);
    }

    [Fact]
    public void FewDaysPullTheFactorTowardsOne()
    {
        var (forecasts, samples) = PvDays(days: 3, forecastKwh: 1.0, measuredKwh: 0.8);

        var correction = PvForecastCorrectionLearner.Learn(forecasts, samples, Berlin, Now);

        Assert.False(correction.IsReady);
        Assert.Equal(1 - 0.2 * 3 / 7d, correction.OverallFactor, 3);
    }

    [Fact]
    public void FactorsStayWithinBounds()
    {
        var (forecasts, samples) = PvDays(days: 10, forecastKwh: 1.0, measuredKwh: 0.1);

        Assert.Equal(0.5, PvForecastCorrectionLearner.Learn(forecasts, samples, Berlin, Now).OverallFactor, 3);
    }

    [Fact]
    public void ConsumptionIsLearnedFromDrivesBetweenCharges()
    {
        var samples = new List<CarSample>();
        var at = Now.AddDays(-10);
        var odometer = 10000d;
        for (var drive = 0; drive < 3; drive++)
        {
            // Several reports during the drive, then charging back to 80 %.
            samples.Add(new CarSample(at, 80, odometer));
            samples.Add(new CarSample(at.AddMinutes(20), 74.5, odometer + 25));
            samples.Add(new CarSample(at.AddMinutes(40), 68.9, odometer + 50)); // 11.1 % of 81 kWh = 9 kWh for 50 km
            samples.Add(new CarSample(at.AddHours(5), 80, odometer + 50));
            at = at.AddDays(1);
            odometer += 50;
        }

        var learned = CarConsumptionLearner.Learn(samples, 81, null, 1.2);

        Assert.NotNull(learned);
        Assert.Equal(3, learned.Drives);
        Assert.Equal(18.0, learned.KwhPer100Km, 1);
        Assert.Equal(150, learned.TotalKm);
    }

    [Fact]
    public void StandbyLossesWhileParkedDoNotCountAsDriving()
    {
        var samples = new[]
        {
            new CarSample(Now.AddDays(-2), 80, 10000),
            new CarSample(Now.AddDays(-2).AddMinutes(30), 75, 10030),
            // Parked overnight, 2 % lost without driving, then a short drive.
            new CarSample(Now.AddDays(-1), 73, 10030),
            new CarSample(Now.AddDays(-1).AddMinutes(30), 70, 10045)
        };

        // Only two drives: below the three needed, but the parked drop must not have merged them into one long drive.
        Assert.Null(CarConsumptionLearner.Learn(samples, 81, null, 0));
    }

    [Fact]
    public void ColdDrivesAreNormalisedToMildWeather()
    {
        var samples = new List<CarSample>();
        var at = Now.AddDays(-10);
        for (var drive = 0; drive < 3; drive++)
        {
            samples.Add(new CarSample(at, 80, 10000 + drive * 100));
            samples.Add(new CarSample(at.AddMinutes(40), 80 - 13.1, 10050 + drive * 100)); // 10.6 kWh / 50 km = 21.2 kWh/100 km
            samples.Add(new CarSample(at.AddHours(5), 80, 10050 + drive * 100));
            at = at.AddDays(1);
        }

        var temperatures = Enumerable.Range(0, 24 * 12).Select(hour => new HourlyTemperature(Now.AddDays(-11).AddHours(hour), 0m)).ToArray();

        var learned = CarConsumptionLearner.Learn(samples, 81, temperatures, 1.2);

        // 0 degrees means +18 %: 21.2 / 1.18 = 18.0 in mild weather.
        Assert.Equal(18.0, learned!.KwhPer100Km, 1);
    }

    private static (List<PvForecastSnapshot> Forecasts, List<LiveConsumptionSample> Samples) PvDays(int days, double forecastKwh, double measuredKwh)
    {
        var forecasts = new List<PvForecastSnapshot>();
        var samples = new List<LiveConsumptionSample>();
        for (var day = 1; day <= days; day++)
        {
            // Four quarter hours from 10:00 UTC (11:00 Berlin).
            var hour = new DateTimeOffset(Now.Date.AddDays(-day).AddHours(10), TimeSpan.Zero);
            for (var quarter = 0; quarter < 4; quarter++)
            {
                var start = hour.AddMinutes(15 * quarter);
                forecasts.Add(new PvForecastSnapshot(start, (decimal)forecastKwh));
                for (var sample = 0; sample < 3; sample++)
                    samples.Add(new LiveConsumptionSample(500, start.AddMinutes(1 + 4 * sample), 0, 0, 50, 0)
                        { SupplementaryPvProductionWatts = (decimal)(measuredKwh / 0.25 * 1000) });
            }
        }

        return (forecasts, samples);
    }
}
