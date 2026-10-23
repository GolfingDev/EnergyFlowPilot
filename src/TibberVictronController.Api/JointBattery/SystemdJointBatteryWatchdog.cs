using System.Net.Sockets;
using System.Text;

namespace TibberVictronController.Api.JointBattery;

public interface IJointBatteryWatchdog
{
    bool IsSupervised { get; }
    Task PulseAsync(CancellationToken token);
}

/// <summary>Only the execution loop pulses this watchdog, so a stalled controller is detected outside its process.</summary>
public sealed class SystemdJointBatteryWatchdog : IJointBatteryWatchdog
{
    // Must match WatchdogSec in the deployment scripts. The execution loop pulses before and after its
    // budgeted steps (4 s tick, 6 s failure handling, 1 s pause), so pulses are at most ~7 s apart and
    // 15 s leaves a 2x margin. A longer interval would no longer bound how long a stalled process keeps stale setpoints.
    private const long MaximumWatchdogMicroseconds = 15_000_000;

    public bool IsSupervised => OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET")) &&
        long.TryParse(Environment.GetEnvironmentVariable("WATCHDOG_USEC"), out var micros) && micros is > 0 and <= MaximumWatchdogMicroseconds;
    public Task PulseAsync(CancellationToken token) => NotifyAsync("WATCHDOG=1", token);
    public Task ReadyAsync(CancellationToken token) => NotifyAsync("READY=1\nWATCHDOG=1", token);
    private static async Task NotifyAsync(string payload, CancellationToken token)
    {
        var path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path)) return;
        if (path[0] == '@') path = "\0" + path[1..];
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        await socket.SendToAsync(Encoding.UTF8.GetBytes(payload), SocketFlags.None, new UnixDomainSocketEndPoint(path), token);
    }
}
