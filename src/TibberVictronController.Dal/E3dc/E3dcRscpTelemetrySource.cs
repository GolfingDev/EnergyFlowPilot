using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.E3dc;

/// <summary>Strictly read-only RSCP adapter. Uses the shared session so polling does not consume extra device logins.</summary>
public sealed class E3dcRscpTelemetrySource(IControllerSettingStore settings, IUtcClock clock, E3dcRscpSession? session = null) : IE3dcTelemetrySource
{
    private readonly E3dcRscpSession rscp = session ?? new E3dcRscpSession();

    public async Task<E3dcLocalTelemetry> ReadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var snapshot = await rscp.ExecuteAsync(settings, ReadSnapshotAsync, timeout.Token);
        if (snapshot.SocPercent is < 0 or > 100 || snapshot.UsableCapacityKwh is <= 0 or > 1000 || snapshot.ReserveKwh < 0 ||
            snapshot.ReserveKwh >= snapshot.UsableCapacityKwh || snapshot.MaximumChargeWatts is <= 0 or > 100000 ||
            snapshot.MaximumDischargeWatts is <= 0 or > 100000 ||
            snapshot.EffectiveChargeLimitWatts < 0 || snapshot.EffectiveDischargeLimitWatts < 0)
            throw new InvalidDataException("Die lokalen E3/DC-Grenzen sind unplausibel.");
        return snapshot;
    }

    private async Task<E3dcLocalTelemetry> ReadSnapshotAsync(RscpProtocol protocol, CancellationToken token)
    {
        async Task<RscpValue> Read(uint tag) => await protocol.ExchangeAsync(RscpValue.Request(tag), tag | 0x00800000, token);
        async Task<int> Number(uint tag) => checked((int)(await Read(tag)).Number());
        var startedAt = clock.UtcNow;
        var battery = await protocol.ExchangeAsync(RscpValue.Container(0x03040000,
            new(0x03040001, 5, [0, 0]), RscpValue.Request(0x03000001),
            RscpValue.Request(0x03000026), RscpValue.Request(0x03000046)), 0x03840000, token);
        var capacity = battery.Required(0x03800026).Number() * battery.Required(0x03800046).Number() / 1000m;
        var reserve = (await Read(0x1B000009)).Required(0x1B040033).Number() / 1000m;
        var specifications = await Read(0x01000234);
        var charge = checked((int)specifications.Required(0x01800235).Number());
        var discharge = checked((int)specifications.Required(0x01800236).Number());
        var powerSettings = await Read(0x0100008B);
        return new E3dcLocalTelemetry(startedAt, battery.Required(0x03800001).Number(),
            await Number(0x01000002), capacity, reserve, charge, discharge,
            await Number(0x01000041), await Number(0x01000045), await Number(0x01000011),
            powerSettings.Required(0x01000105).Number() != 0);
    }
}
