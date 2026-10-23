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
}
