namespace TibberVictronController.Dal.Entities;

/// <summary>One published retail price quarter hour of a supplier (the price that applied, not an estimate).</summary>
public sealed class PublishedPriceSlotEntity
{
    public string Source { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset SlotStartUtc { get; set; }
    public DateTimeOffset SlotEndUtc { get; set; }
    public decimal PricePerKwh { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    /// <summary><c>provider</c> (recorded from the supplier) or <c>decisionLog</c> (imported from older decision logs).</summary>
    public string Origin { get; set; } = string.Empty;
}
