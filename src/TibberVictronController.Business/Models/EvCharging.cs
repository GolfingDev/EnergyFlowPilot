namespace TibberVictronController.Business.Models;

/// <summary>Connector state as reported by the wallbox (OCPP 1.6 StatusNotification).</summary>
public enum EvConnectorStatus
{
    Unknown,
    Available,
    Preparing,
    Charging,
    SuspendedEv,
    SuspendedEvse,
    Finishing,
    Reserved,
    Unavailable,
    Faulted
}

/// <summary>One charging session (OCPP transaction). Meter readings are in Wh as reported by the wallbox.</summary>
/// <summary>How a car below the base state of charge charges: at once, or in the cheapest hours within a day.</summary>
public enum EvBaseChargeMode
{
    PriceDriven,
    Immediate
}

public sealed record EvChargingSession(
    int TransactionId,
    string ChargePointId,
    int ConnectorId,
    string IdTag,
    DateTimeOffset StartedAtUtc,
    double MeterStartWh,
    DateTimeOffset? StoppedAtUtc,
    double? MeterStopWh,
    string? StopReason,
    double? LastMeterWh,
    DateTimeOffset? LastMeterAtUtc)
{
    public bool IsActive => StoppedAtUtc is null;

    /// <summary>The driver's answer for this charging session; null while nobody has decided.</summary>
    public EvBaseChargeMode? BaseChargeMode { get; init; }

    public DateTimeOffset? BaseChoiceAskedAtUtc { get; init; }

    public string? BaseChoiceBy { get; init; }

    /// <summary>Charging stopped by hand for this session (0 A) until resumed or the car is plugged in again.</summary>
    public bool IsStoppedByUser { get; init; }

    public string? StoppedBy { get; init; }

    /// <summary>Charge at once up to this state of charge in this session (set by hand), then follow the plan.</summary>
    public double? ImmediateTargetPercent { get; init; }

    public string? ImmediateTargetBy { get; init; }

    // A meter reset or a wrong unit must not show negative energy.
    public double EnergyKwh => Math.Max(0, ((MeterStopWh ?? LastMeterWh ?? MeterStartWh) - MeterStartWh) / 1000d);
}

/// <summary>Start data of a session; identical repeated starts (charger resend after reconnect) map to one session.</summary>
public sealed record EvChargingSessionStart(
    string ChargePointId,
    int ConnectorId,
    string IdTag,
    DateTimeOffset StartedAtUtc,
    double MeterStartWh);

/// <summary>Live state of the wallbox connection. Values stay null until the wallbox has reported them.</summary>
public sealed record EvChargerSnapshot
{
    public static readonly EvChargerSnapshot Empty = new();

    public bool IsConnected { get; init; }
    public string? ChargePointId { get; init; }
    public DateTimeOffset? ConnectedSinceUtc { get; init; }
    public DateTimeOffset? LastMessageAtUtc { get; init; }
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? FirmwareVersion { get; init; }
    public EvConnectorStatus ConnectorStatus { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset? StatusAtUtc { get; init; }
    public int? ActiveTransactionId { get; init; }
    public DateTimeOffset? SessionStartedAtUtc { get; init; }
    public double? SessionEnergyKwh { get; init; }
    public double? MeterWh { get; init; }
    public double? PowerWatts { get; init; }
    public bool PowerIsDerived { get; init; }
    public DateTimeOffset? MeasuredAtUtc { get; init; }
    public double? CurrentL1Amps { get; init; }
    public double? CurrentL2Amps { get; init; }
    public double? CurrentL3Amps { get; init; }
    public string? ConfigurationNote { get; init; }
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } = new Dictionary<string, string?>();
    public EvConnectionRejection? LastRejection { get; init; }
    public EvChargerCommandResult? LastCommand { get; init; }

    // An open transaction also counts: the wallbox ends it when the car is unplugged, but after a reconnect it may
    // report its connector state only with the next change (operator 2026-11-03: plugged in, page said otherwise).
    public bool IsVehiclePlugged => ConnectorStatus is EvConnectorStatus.Preparing or EvConnectorStatus.Charging
        or EvConnectorStatus.SuspendedEv or EvConnectorStatus.SuspendedEvse or EvConnectorStatus.Finishing
        || ActiveTransactionId is not null;
}

/// <summary>Outcome of a current command: the wallbox's own answer (Accepted, Rejected, NotSupported) and what was sent.</summary>
public sealed record EvChargerCommandResult(
    bool Accepted,
    string Status,
    string Description,
    DateTimeOffset AtUtc,
    DateTimeOffset? ValidUntilUtc);

/// <summary>Last refused connection attempt on the OCPP port, so the operator can see what the wallbox tried.</summary>
public sealed record EvConnectionRejection(
    string ChargePointId,
    string Reason,
    DateTimeOffset AtUtc,
    string? RemoteAddress,
    string RequestedPath);
