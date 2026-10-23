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
    public bool IsSupervised => OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET")) &&
        long.TryParse(Environment.GetEnvironmentVariable("WATCHDOG_USEC"), out var micros) && micros is > 0 and <= 5000000;
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
