namespace TibberVictronController.Business.Models;

/// <summary>
/// Explains why the Decision Engine selected a specific instruction.
/// </summary>
public sealed record BatteryDecisionReason
{
    /// <summary>
    /// Keeps decision explanations structured enough for logs, persistence and frontend display.
    /// </summary>
    public BatteryDecisionReason(string ruleName, string message)
    {
        if (string.IsNullOrWhiteSpace(ruleName))
        {
            throw new ArgumentException("Der Regelname der Entscheidungsbegründung muss angegeben werden.", nameof(ruleName));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Die Entscheidungsbegründung muss angegeben werden.", nameof(message));
        }

        RuleName = ruleName;
        Message = message;
    }

    /// <summary>
    /// Gets the name of the rule that produced this reason.
    /// </summary>
    public string RuleName { get; }

    /// <summary>
    /// Gets the human-readable German explanation.
    /// </summary>
    public string Message { get; }
}
