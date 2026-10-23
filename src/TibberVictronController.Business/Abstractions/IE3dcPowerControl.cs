using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IE3dcPowerControl
{
    Task<E3dcPowerAcknowledgement> ApplyAsync(E3dcPowerCommand command, CancellationToken token = default);
}
public enum E3dcPowerMode : byte { Automatic = 0, Idle = 1, Discharge = 2, ChargePv = 3, ChargeGrid = 4 }
public sealed record E3dcPowerCommand(E3dcPowerMode Mode, int PowerWatts);
public sealed record E3dcPowerAcknowledgement(int EchoPowerWatts, int OperatingMode, int MeasuredPowerWatts, DateTimeOffset MeasuredAtUtc);
