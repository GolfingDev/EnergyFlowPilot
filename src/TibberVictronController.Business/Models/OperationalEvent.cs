namespace TibberVictronController.Business.Models;

/// <summary>
/// Represents a persisted technical or operational event for diagnostics.
/// </summary>
public sealed record OperationalEvent
{
    /// <summary>
    /// Validates diagnostic event data before persistence.
    /// </summary>
    public OperationalEvent(
        Guid id,
        DateTimeOffset occurredAtUtc,
        string category,
        string severity,
        string message,
        string? details)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Die Event-ID darf nicht leer sein.", nameof(id));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Event-Zeitpunkt muss in UTC angegeben sein.", nameof(occurredAtUtc));
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("Die Event-Kategorie muss angegeben werden.", nameof(category));
        }

        if (string.IsNullOrWhiteSpace(severity))
        {
            throw new ArgumentException("Der Event-Schweregrad muss angegeben werden.", nameof(severity));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Die Event-Meldung muss angegeben werden.", nameof(message));
        }

        Id = id;
        OccurredAtUtc = occurredAtUtc;
        Category = category;
        Severity = severity;
        Message = message;
        Details = details;
    }

    public Guid Id { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string Category { get; }

    public string Severity { get; }

    public string Message { get; }

    public string? Details { get; }
}
