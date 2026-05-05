namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents the result of evaluating one or more Decision Engine rules.
/// </summary>
public sealed record BatteryDecisionRuleResult
{
    /// <summary>
    /// Requires every selected instruction to carry at least one explanation.
    /// </summary>
    public BatteryDecisionRuleResult(
        BatteryDecisionInstruction instruction,
        IReadOnlyList<BatteryDecisionReason> reasons)
    {
        if (reasons.Count == 0)
        {
            throw new ArgumentException("Eine Entscheidung muss mindestens eine Begruendung enthalten.", nameof(reasons));
        }

        Instruction = instruction;
        Reasons = reasons;
    }

    /// <summary>
    /// Gets the instruction selected by the Decision Engine rules.
    /// </summary>
    public BatteryDecisionInstruction Instruction { get; }

    /// <summary>
    /// Gets the structured reasons for the selected instruction.
    /// </summary>
    public IReadOnlyList<BatteryDecisionReason> Reasons { get; }
}
