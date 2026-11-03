using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvSocEstimatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 16, 5, 0, TimeSpan.Zero);

    [Fact]
    public void EnergyChargedSinceASilentReportIsAdded()
    {
        // Operator 2026-11-03: BMW said 26 % (reported 50 minutes into the session) while the wallbox charged 6.4 kWh more.
        var samples = new[] { new WallboxMeterSample(Start.AddMinutes(40), 1_879_224), new WallboxMeterSample(Start.AddMinutes(60), 1_879_224) };

        var charged = EvSocEstimator.ChargedSinceKwh(Start.AddMinutes(47), Start, 1_879_217, samples, 1_885_663);

        Assert.Equal(6.4 * 0.9, charged, 1);
        var car = CarSnapshot.Empty with { StateOfChargePercent = 26, ChargedSinceSocKwh = charged };
        Assert.Equal(34.6, car.StateOfChargeEstimatePercent(67)!.Value, 1);
    }

    [Fact]
    public void MeterAtTheReportIsInterpolatedAndAReportBeforeTheSessionCountsTheWholeSession()
    {
        var samples = new[] { new WallboxMeterSample(Start.AddHours(1), 11_000) };

        // Halfway between session start (1000 Wh) and the first reading (11 000 Wh): 6000 Wh at the report.
        Assert.Equal(5 * 0.9, EvSocEstimator.ChargedSinceKwh(Start.AddMinutes(30), Start, 1000, samples, 11_000), 6);
        Assert.Equal(10 * 0.9, EvSocEstimator.ChargedSinceKwh(Start.AddHours(-2), Start, 1000, samples, 11_000), 6);
        Assert.Equal(100, (CarSnapshot.Empty with { StateOfChargePercent = 95, ChargedSinceSocKwh = 20 }).StateOfChargeEstimatePercent(67));
    }

    [Fact]
    public void AnOpenTransactionMeansACarIsPluggedInEvenWithoutAConnectorStatus()
    {
        Assert.True((EvChargerSnapshot.Empty with { ConnectorStatus = EvConnectorStatus.Unknown, ActiveTransactionId = 1 }).IsVehiclePlugged);
        Assert.False((EvChargerSnapshot.Empty with { ConnectorStatus = EvConnectorStatus.Available }).IsVehiclePlugged);
    }
}
