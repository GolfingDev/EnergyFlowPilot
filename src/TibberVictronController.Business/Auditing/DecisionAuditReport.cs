namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Contains candidate decisions, baseline decisions and the metrics needed for user review.
/// </summary>
public sealed record DecisionAuditReport(
    DecisionAuditScenario Scenario,
    IReadOnlyList<DecisionAuditSlot> DecisionSlots,
    IReadOnlyList<DecisionAuditSlot> BaselineDecisionSlots,
    DecisionAuditMetrics Metrics,
    DecisionAuditMetrics BaselineMetrics);
