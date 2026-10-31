using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public static class EvChargingEndpoints
{
    public static IEndpointRouteBuilder MapEvChargingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ev-charging/status", GetStatusAsync)
            .WithName("GetEvChargingStatus")
            .WithTags("EvCharging");
        return endpoints;
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext http,
        IEvChargerStatusSource status,
        IEvChargingSessionRepository sessions,
        IControllerSettingStore settings,
        EvChargingOptions options,
        ControllerAuthenticationOptions authentication,
        CancellationToken cancellationToken)
    {
        var enabled = bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.EvOcppEnabledKey, cancellationToken))?.Value, out var value) && value;
        var configuredId = (await settings.GetSettingAsync(ControllerSettingDefaults.EvOcppChargePointIdKey, cancellationToken))?.Value;
        var recent = await sessions.GetRecentAsync(5, cancellationToken);
        // Setup details (rejected ids, addresses, wallbox configuration) are for the admin who sets the wallbox up.
        var isAdmin = !authentication.IsEntra || http.User.IsInRole(AppRoles.Admin);
        return TypedResults.Ok(EvChargingStatusDtoMapper.Map(new EvChargingStatusInput(
            status.Current, recent, enabled,
            !string.IsNullOrWhiteSpace(configuredId) && configuredId != ControllerSettingDefaults.EvOcppChargePointNotConfigured,
            isAdmin, isAdmin ? LanAddresses(options.OcppListenPort) : [])));
    }

    /// <summary>Candidate addresses for the wallbox setup; Tailscale (100.64.0.0/10) and link-local are not reachable for it.</summary>
    private static IReadOnlyList<string> LanAddresses(int port)
    {
        if (port == 0) return [];
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !IsTailscaleOrLinkLocal(address))
                .Select(address => $"ws://{address}:{port}{OcppEndpoints.OcppPath}/")
                .Distinct()
                .ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static bool IsTailscaleOrLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) || (bytes[0] == 169 && bytes[1] == 254);
    }
}

public sealed record EvChargingStatusInput(
    EvChargerSnapshot Snapshot,
    IReadOnlyList<EvChargingSession> RecentSessions,
    bool OcppEnabled,
    bool ChargePointConfigured,
    bool IncludeSetupDetails,
    IReadOnlyList<string> ConnectionUrls);

public static class EvChargingStatusDtoMapper
{
    public static EvChargingStatusResponseDto Map(EvChargingStatusInput input)
    {
        var snapshot = input.Snapshot;
        var setup = input.IncludeSetupDetails;
        return new EvChargingStatusResponseDto(
            input.OcppEnabled,
            input.ChargePointConfigured,
            snapshot.IsConnected,
            snapshot.ConnectedSinceUtc,
            snapshot.LastMessageAtUtc,
            snapshot.Vendor,
            snapshot.Model,
            snapshot.FirmwareVersion,
            snapshot.ConnectorStatus.ToString(),
            StatusLabel(snapshot.ConnectorStatus),
            snapshot.IsVehiclePlugged,
            snapshot.ConnectorStatus == EvConnectorStatus.Charging,
            snapshot.ErrorCode,
            snapshot.PowerWatts,
            snapshot.PowerIsDerived,
            snapshot.MeasuredAtUtc,
            snapshot.ActiveTransactionId is not null,
            snapshot.SessionStartedAtUtc,
            snapshot.SessionEnergyKwh,
            snapshot.CurrentL1Amps,
            snapshot.CurrentL2Amps,
            snapshot.CurrentL3Amps,
            snapshot.ConfigurationNote,
            setup ? snapshot.LastRejectedChargePointId : null,
            setup ? snapshot.LastRejectedReason : null,
            setup ? snapshot.LastRejectedAtUtc : null,
            input.ConnectionUrls,
            setup ? snapshot.Configuration : new Dictionary<string, string?>(),
            input.RecentSessions.Select(session => new EvChargingSessionDto(
                session.TransactionId, session.StartedAtUtc, session.StoppedAtUtc, session.EnergyKwh, session.StopReason)).ToArray());
    }

    private static string StatusLabel(EvConnectorStatus status) => status switch
    {
        EvConnectorStatus.Available => "Frei, kein Auto angesteckt",
        EvConnectorStatus.Preparing => "Auto angesteckt, wartet",
        EvConnectorStatus.Charging => "Lädt",
        EvConnectorStatus.SuspendedEv => "Pausiert durch das Auto",
        EvConnectorStatus.SuspendedEvse => "Pausiert durch die Wallbox",
        EvConnectorStatus.Finishing => "Ladevorgang beendet, Auto noch angesteckt",
        EvConnectorStatus.Reserved => "Reserviert",
        EvConnectorStatus.Unavailable => "Nicht verfügbar",
        EvConnectorStatus.Faulted => "Störung",
        _ => "Unbekannt"
    };
}

public sealed record EvChargingStatusResponseDto(
    bool OcppEnabled,
    bool ChargePointConfigured,
    bool IsConnected,
    DateTimeOffset? ConnectedSinceUtc,
    DateTimeOffset? LastMessageAtUtc,
    string? Vendor,
    string? Model,
    string? FirmwareVersion,
    string ConnectorStatus,
    string ConnectorStatusLabel,
    bool VehiclePlugged,
    bool IsCharging,
    string? ErrorCode,
    double? PowerWatts,
    bool PowerIsDerived,
    DateTimeOffset? MeasuredAtUtc,
    bool SessionActive,
    DateTimeOffset? SessionStartedAtUtc,
    double? SessionEnergyKwh,
    double? CurrentL1Amps,
    double? CurrentL2Amps,
    double? CurrentL3Amps,
    string? ConfigurationNote,
    string? LastRejectedChargePointId,
    string? LastRejectedReason,
    DateTimeOffset? LastRejectedAtUtc,
    IReadOnlyList<string> ConnectionUrls,
    IReadOnlyDictionary<string, string?> WallboxConfiguration,
    IReadOnlyList<EvChargingSessionDto> RecentSessions);

public sealed record EvChargingSessionDto(
    int TransactionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    double EnergyKwh,
    string? StopReason);
