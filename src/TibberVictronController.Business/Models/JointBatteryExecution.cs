namespace TibberVictronController.Business.Models;

public sealed record JointBatteryElectricalLimits(IReadOnlyList<int> PhaseImportWatts, int VictronAcInputWatts,
    int VictronPhaseCount, int TotalImportWatts);
public sealed record JointBatteryLiveSnapshot(DateTimeOffset MeasuredAtUtc, IReadOnlyList<JointBatteryUnit> Batteries,
    IReadOnlyList<int> PhaseGridWatts, IReadOnlyList<int> AcOutWatts, int PrimaryAcBatteryWatts,
    int E3dcOperatingMode, JointBatteryElectricalLimits Limits);
public sealed record JointBatteryCommand(int VictronWatts, int E3dcWatts, BatteryChargeSource? ChargeSource,
    DateTimeOffset ValidUntilUtc, string Reason);
public sealed record JointBatteryExecutionSnapshot(string Status, string Message, DateTimeOffset UpdatedAtUtc,
    bool OwnsControl, int VictronTargetWatts = 0, int E3dcTargetWatts = 0,
    int VictronMeasuredWatts = 0, int E3dcMeasuredWatts = 0, DateTimeOffset? ValidUntilUtc = null);

/// <summary>Process-local state is shared between execution scopes; the recovery obligation is also persisted separately.</summary>
public sealed class JointBatteryExecutionState
{
    private JointBatteryExecutionSnapshot current = new("Disabled", "Gemeinsame Hardwareausfuehrung deaktiviert.", DateTimeOffset.MinValue, false);
    public JointBatteryExecutionSnapshot Current => Volatile.Read(ref current);
    public void Set(JointBatteryExecutionSnapshot snapshot) => Interlocked.Exchange(ref current, snapshot);
    public int Direction { get; set; }
    public int NeutralSamples { get; set; }
    public DateTimeOffset? SettlingSinceUtc { get; set; }
    public DateTimeOffset? BlockedUntilUtc { get; set; }

    /// <summary>Outcome of the last finished manual charge, shown on the dashboard until the next one starts.</summary>
    public string? ManualChargeResult { get; set; }

    private JointBatterySlot? committedSlot;
    /// <summary>Plan decision fixed for the quarter hour it belongs to; later plans do not change it.</summary>
    public JointBatterySlot? CommittedSlot { get => Volatile.Read(ref committedSlot); set => Volatile.Write(ref committedSlot, value); }

    private JointBatteryLiveSnapshot? lastLive;
    /// <summary>Most recent live reading of both batteries; lets a manual charge measure its progress without an extra device read.</summary>
    public JointBatteryLiveSnapshot? LastLive { get => Volatile.Read(ref lastLive); set => Volatile.Write(ref lastLive, value); }
}
