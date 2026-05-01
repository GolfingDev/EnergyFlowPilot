namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents the compact command intent produced by the Decision Engine for forecast entries and direct decisions.
/// </summary>
public sealed record BatteryDecisionInstruction
{
    /// <summary>
    /// Keeps the three Decision Engine states explicit while preventing invalid charge-source combinations.
    /// </summary>
    public BatteryDecisionInstruction(BatteryDecisionState decisionState, BatteryChargeSource? chargeSource)
    {
        if (decisionState == BatteryDecisionState.Charge && chargeSource is null)
        {
            throw new ArgumentException("Bei einer Ladeentscheidung muss die Ladequelle angegeben werden.", nameof(chargeSource));
        }

        if (decisionState != BatteryDecisionState.Charge && chargeSource is not null)
        {
            throw new ArgumentException("Eine Ladequelle darf nur bei einer Ladeentscheidung angegeben werden.", nameof(chargeSource));
        }

        DecisionState = decisionState;
        ChargeSource = chargeSource;
    }

    /// <summary>
    /// Gets the operating state selected by the Decision Engine.
    /// </summary>
    public BatteryDecisionState DecisionState { get; }

    /// <summary>
    /// Gets the selected charge source when the Decision Engine state is charge.
    /// </summary>
    public BatteryChargeSource? ChargeSource { get; }
}
