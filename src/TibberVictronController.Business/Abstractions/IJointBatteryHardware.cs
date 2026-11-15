using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

public interface IJointBatteryHardware
{
    /// <summary>
    /// Reads both batteries without writing anything. Throws <see cref="JointBatteryReadMissException"/> only when the
    /// measurements merely did not arrive in time; every other failure is an ordinary exception.
    /// </summary>
    Task<JointBatteryLiveSnapshot> ReadAsync(CancellationToken token);
    Task SetVictronAsync(int signedWatts, CancellationToken token);
    Task<E3dcPowerAcknowledgement> SetE3dcAsync(E3dcPowerCommand command, CancellationToken token);
}

/// <summary>
/// The measurements of one tick did not arrive in time; the read itself wrote nothing and found nothing wrong.
/// A typed signal instead of message matching: only <see cref="Decisions.JointBatteryExecutionCoordinator"/> may
/// tolerate it, and only for the read that precedes all writes of a step. It stays a <see cref="TimeoutException"/>,
/// so every other caller handles it exactly like the timeout it replaces.
/// </summary>
public sealed class JointBatteryReadMissException(string message, Exception? innerException = null)
    : TimeoutException(message, innerException);
