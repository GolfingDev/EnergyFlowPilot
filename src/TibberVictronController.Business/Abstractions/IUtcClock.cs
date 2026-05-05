namespace TibberVictronController.Business.Abstractions;

/// <summary>
/// Provides UTC timestamps for application services without coupling business logic to system time.
/// </summary>
public interface IUtcClock
{
    /// <summary>
    /// Gets the current timestamp in UTC.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
