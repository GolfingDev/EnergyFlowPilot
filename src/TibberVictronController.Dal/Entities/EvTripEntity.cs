namespace TibberVictronController.Dal.Entities;

public sealed class EvTripEntity
{
    public int Id { get; set; }
    public DateTimeOffset DepartureAtUtc { get; set; }
    public DateTimeOffset? ReturnAtUtc { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string ResolvedAddress { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double DistanceKm { get; set; }
    public double DurationMinutes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int RecurrenceDays { get; set; }
    public DateTimeOffset? RecurrenceUntilUtc { get; set; }
    public string Source { get; set; } = "Manual";
    public string? ExternalId { get; set; }
    public string? Subject { get; set; }
    public bool IsIgnored { get; set; }
}
