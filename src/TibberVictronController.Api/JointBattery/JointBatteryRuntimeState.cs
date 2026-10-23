using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

public sealed record JointBatteryRuntimeSnapshot(string Status, string Message, DateTimeOffset UpdatedAtUtc,
    E3dcLocalTelemetry? LocalTelemetry, JointBatteryPlan? Plan);

/// <summary>Immutable snapshots are shared across the polling worker and HTTP requests.</summary>
public sealed class JointBatteryRuntimeState
{
    private JointBatteryRuntimeSnapshot current = new("Disabled", "Die gemeinsame Vorschau ist deaktiviert.",
        DateTimeOffset.MinValue, null, null);
    public JointBatteryRuntimeSnapshot Current => Volatile.Read(ref current);
    public void Set(JointBatteryRuntimeSnapshot snapshot) => Interlocked.Exchange(ref current, snapshot);
}
