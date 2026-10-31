using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Abstractions;

/// <summary>Persists wallbox charging sessions; transaction ids are never reused.</summary>
public interface IEvChargingSessionRepository
{
    /// <summary>Starts a session. A repeated identical start returns the existing session.</summary>
    Task<EvChargingSession> StartAsync(EvChargingSessionStart start, CancellationToken cancellationToken = default);

    Task<EvChargingSession?> GetAsync(int transactionId, CancellationToken cancellationToken = default);

    Task UpdateMeterAsync(int transactionId, double meterWh, DateTimeOffset measuredAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Stops a session; returns null for an unknown transaction.</summary>
    Task<EvChargingSession?> StopAsync(int transactionId, double meterStopWh, DateTimeOffset stoppedAtUtc, string? reason, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvChargingSession>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default);
}

/// <summary>Live wallbox state for the dashboard and, later, the charging planner.</summary>
public interface IEvChargerStatusSource
{
    EvChargerSnapshot Current { get; }
}
