using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.BmwCarData;

namespace TibberVictronController.Api.EvCharging;

/// <summary>
/// The car values for planning: BMW's last report plus the energy the wallbox has charged since (see
/// <see cref="EvSocEstimator"/>). Keeps a day of wallbox meter readings to know the meter at the time of the report.
/// The raw store stays untouched, so learning and the saved snapshot use only what BMW reported.
/// </summary>
public sealed class EstimatingCarStatusSource(CarStatusStore store, IEvChargerStatusSource charger) : ICarStatusSource
{
    private static readonly TimeSpan HistoryLength = TimeSpan.FromHours(26);
    private readonly object gate = new();
    private readonly List<WallboxMeterSample> samples = [];

    public CarSnapshot Car
    {
        get
        {
            var car = store.Car;
            var wallbox = charger.Current;
            Record(wallbox);
            if (car is not { StateOfChargePercent: not null, StateOfChargeAtUtc: { } socAt } ||
                wallbox is not { ActiveTransactionId: not null, SessionStartedAtUtc: { } start, MeterWh: { } meterNow })
                return car;
            var meterStart = meterNow - (wallbox.SessionEnergyKwh ?? 0) * 1000;
            WallboxMeterSample[] known;
            lock (gate) known = samples.ToArray();
            return car with { ChargedSinceSocKwh = EvSocEstimator.ChargedSinceKwh(socAt, start, meterStart, known, meterNow) };
        }
    }

    public CarDataConnectionState Connection => store.Connection;

    public CarStreamDiagnostics Stream => store.Stream;

    public CarPollDiagnostics Poll => store.Poll;

    // Read every half minute by the charging control, which is often enough for the history.
    private void Record(EvChargerSnapshot wallbox)
    {
        if (wallbox is not { MeterWh: { } meter, MeasuredAtUtc: { } at }) return;
        lock (gate)
        {
            if (samples.Count > 0 && samples[^1].AtUtc >= at) return;
            samples.Add(new WallboxMeterSample(at, meter));
            samples.RemoveAll(sample => sample.AtUtc < at - HistoryLength);
        }
    }
}
