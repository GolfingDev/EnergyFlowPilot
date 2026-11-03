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

    Task MarkBaseChoiceAskedAsync(int transactionId, DateTimeOffset askedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Stores the driver's answer; false when the session is unknown or already finished.</summary>
    Task<bool> SetBaseChargeModeAsync(int transactionId, EvBaseChargeMode mode, string decidedBy, CancellationToken cancellationToken = default);

    /// <summary>Stops or resumes charging of a running session by hand; false when it is unknown or finished.</summary>
    Task<bool> SetStoppedByUserAsync(int transactionId, bool stopped, string by, CancellationToken cancellationToken = default);

    /// <summary>Sets (null: removes) the immediate charge target of a running session; false when it is unknown or finished.</summary>
    Task<bool> SetImmediateTargetAsync(int transactionId, double? targetPercent, string by, CancellationToken cancellationToken = default);
}

/// <summary>Live wallbox state for the dashboard and, later, the charging planner.</summary>
public interface IEvChargerStatusSource
{
    EvChargerSnapshot Current { get; }
}

/// <summary>
/// Charging current commands to the connected wallbox. Every limit expires by itself, so the wallbox returns to full
/// power if the controller stops sending (rule 1: never stand still for missing data).
/// </summary>
public interface IEvChargerControl
{
    /// <summary>0 A pauses charging; otherwise 6 to 16 A on three phases.</summary>
    Task<EvChargerCommandResult> LimitCurrentAsync(double amps, TimeSpan validity, CancellationToken cancellationToken = default);

    Task<EvChargerCommandResult> ClearLimitAsync(CancellationToken cancellationToken = default);
}
