namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents the actionable Decision Engine decision for the current point in time.
/// </summary>
public sealed record CurrentBatteryDecision
{
    /// <summary>
    /// Validates that the direct decision contains a usable watt setpoint for active energy movement.
    /// </summary>
    public CurrentBatteryDecision(BatteryDecisionInstruction instruction, int targetPowerWatts)
    {
        if (targetPowerWatts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPowerWatts), "Die Ziel-Leistung darf nicht negativ sein.");
        }

        if (instruction.DecisionState == BatteryDecisionState.Idle && targetPowerWatts != 0)
        {
            throw new ArgumentException("Bei Idle muss die Ziel-Leistung 0 Watt betragen.", nameof(targetPowerWatts));
        }

        if (instruction.DecisionState != BatteryDecisionState.Idle && targetPowerWatts == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPowerWatts), "Beim Laden oder Entladen muss die Ziel-Leistung mehr als 0 Watt betragen.");
        }

        Instruction = instruction;
        TargetPowerWatts = targetPowerWatts;
    }

    /// <summary>
    /// Gets the Decision Engine operating instruction for the current decision.
    /// </summary>
    public BatteryDecisionInstruction Instruction { get; }

    /// <summary>
    /// Gets the absolute target power in watts. The direction is defined by the decision state.
    /// </summary>
    public int TargetPowerWatts { get; }
}
