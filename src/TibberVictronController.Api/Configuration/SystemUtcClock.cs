using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Provides the production UTC timestamp source for dependency-injected services.
/// </summary>
public sealed class SystemUtcClock : IUtcClock
{
    /// <summary>
    /// Gets the current system timestamp as UTC.
    /// </summary>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
