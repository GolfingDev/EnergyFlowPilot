using System.Buffers.Binary;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.E3dc;

/// <summary>Only ephemeral power commands are supported. No persistent weather, reserve or power-limit settings are written.</summary>
public sealed class E3dcRscpPowerControl(IControllerSettingStore settings, IUtcClock clock, E3dcRscpSession? session = null) : IE3dcPowerControl
{
    private readonly E3dcRscpSession rscp = session ?? new E3dcRscpSession();

    public async Task<E3dcPowerAcknowledgement> ApplyAsync(E3dcPowerCommand command, CancellationToken token = default)
    {
        if (!Enum.IsDefined(command.Mode) || command.PowerWatts is < 0 or > 100000 ||
            (command.Mode is E3dcPowerMode.Automatic or E3dcPowerMode.Idle && command.PowerWatts != 0) ||
            (command.Mode is E3dcPowerMode.ChargeGrid or E3dcPowerMode.ChargePv or E3dcPowerMode.Discharge && command.PowerWatts == 0))
            throw new ArgumentException("Der E3/DC-Leistungsbefehl ist ungueltig.");
        if (command.Mode is not (E3dcPowerMode.Automatic or E3dcPowerMode.Idle))
        {
            if (!bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.JointBatteryExecutionEnabledKey, token))?.Value, out var enabled) || !enabled)
                throw new InvalidOperationException("Gemeinsame Hardwareausfuehrung ist nicht freigegeben.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        return await rscp.ExecuteAsync(settings, async (protocol, ct) =>
        {
            var power = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(power, command.PowerWatts);
            var reply = await protocol.ExchangeAsync(RscpValue.Container(0x01000030,
                new(0x01000031, 3, [(byte)command.Mode]), new(0x01000032, 6, power)), 0x01800030, ct);
            var mode = await protocol.ExchangeAsync(RscpValue.Request(0x01000011), 0x01800011, ct);
            var measured = await protocol.ExchangeAsync(RscpValue.Request(0x01000002), 0x01800002, ct);
            return new E3dcPowerAcknowledgement(checked((int)reply.Number()), checked((int)mode.Number()), checked((int)measured.Number()), clock.UtcNow);
        }, deadline.Token);
    }
}
