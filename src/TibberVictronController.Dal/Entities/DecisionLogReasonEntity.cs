namespace TibberVictronController.Dal.Entities;

public sealed class DecisionLogReasonEntity
{
    public long Id { get; set; }

    public Guid DecisionLogEntryId { get; set; }

    public string RuleName { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public DecisionLogEntryEntity? DecisionLogEntry { get; set; }
}
