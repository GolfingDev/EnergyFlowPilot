using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IJointBatteryHardware
{
    Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token);
    Task SetVictronAsync(int signedWatts, CancellationToken token);
    Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token);
}
