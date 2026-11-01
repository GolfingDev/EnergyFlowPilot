using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.EvCharging;

public static class EvChargingEndpoints
{
    // Lets the car page say "no attempt since ..." instead of leaving the operator guessing.
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;

    public static IEndpointRouteBuilder MapEvChargingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ev-charging/status", GetStatusAsync)
            .WithName("GetEvChargingStatus")
            .WithTags("EvCharging");
        endpoints.MapPost("/api/ev-charging/wallbox/limit", LimitAsync)
            .WithName("LimitWallboxCurrent")
            .WithTags("EvCharging");
        endpoints.MapDelete("/api/ev-charging/wallbox/limit", ClearLimitAsync)
            .WithName("ClearWallboxLimit")
            .WithTags("EvCharging");
        endpoints.MapGet("/api/ev-charging/car", GetCar)
            .WithName("GetCarStatus")
            .WithTags("EvCharging");
        endpoints.MapPost("/api/ev-charging/car/sign-in", StartCarSignInAsync)
            .WithName("StartCarDataSignIn")
            .WithTags("EvCharging");
        endpoints.MapPost("/api/ev-charging/car/refresh", (CarDataService carData) =>
            {
                carData.RequestPoll();
                return TypedResults.Accepted((string?)null);
            })
            .WithName("RefreshCarData")
            .WithTags("EvCharging");
        endpoints.MapDelete("/api/ev-charging/car/sign-in", SignOutCarAsync)
            .WithName("SignOutCarData")
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
            isAdmin, isAdmin ? LanAddresses(options.OcppListenPort) : [], options.OcppListenPort, StartedAtUtc)));
    }

    private static async Task<IResult> LimitAsync(WallboxLimitRequestDto request, IEvChargerControl control, CancellationToken cancellationToken)
    {
        try
        {
            var result = await control.LimitCurrentAsync(request.Amps, TimeSpan.FromMinutes(request.Minutes), cancellationToken);
            return TypedResults.Ok(result);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
    }

    private static async Task<IResult> ClearLimitAsync(IEvChargerControl control, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await control.ClearLimitAsync(cancellationToken));
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
    }

    private static IResult GetCar(HttpContext http, ICarStatusSource car, ControllerAuthenticationOptions authentication) =>
        TypedResults.Ok(CarStatusDtoMapper.Map(car.Car, car.Connection, car.Stream, !authentication.IsEntra || http.User.IsInRole(AppRoles.Admin)) with
        {
            Poll = new CarPollDiagnosticsDto(car.Poll.LastPollAtUtc, car.Poll.PollsToday, car.Poll.DailyLimit, car.Poll.LastProblem, car.Poll.LastTrigger)
        });

    private static async Task<IResult> StartCarSignInAsync(CarDataService carData, CancellationToken cancellationToken)
    {
        try
        {
            await carData.StartSignInAsync(cancellationToken);
            return TypedResults.NoContent();
        }
        catch (InvalidOperationException exception)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            return TypedResults.Problem($"BMW ist nicht erreichbar: {exception.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> SignOutCarAsync(CarDataService carData, CancellationToken cancellationToken)
    {
        await carData.SignOutAsync(cancellationToken);
        return TypedResults.NoContent();
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

public sealed record WallboxLimitRequestDto(double Amps, double Minutes);

public sealed record EvChargingStatusInput(
    EvChargerSnapshot Snapshot,
    IReadOnlyList<EvChargingSession> RecentSessions,
    bool OcppEnabled,
    bool ChargePointConfigured,
    bool IncludeSetupDetails,
    IReadOnlyList<string> ConnectionUrls,
    int OcppListenPort,
    DateTimeOffset ListeningSinceUtc);

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
            setup && snapshot.LastRejection is { } rejection
                ? new EvConnectionRejectionDto(rejection.ChargePointId, rejection.Reason, rejection.AtUtc, rejection.RemoteAddress, rejection.RequestedPath)
                : null,
            input.ConnectionUrls,
            input.OcppListenPort,
            input.ListeningSinceUtc,
            setup ? snapshot.Configuration : new Dictionary<string, string?>(),
            snapshot.LastCommand,
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
    EvConnectionRejectionDto? LastRejection,
    IReadOnlyList<string> ConnectionUrls,
    int OcppListenPort,
    DateTimeOffset ListeningSinceUtc,
    IReadOnlyDictionary<string, string?> WallboxConfiguration,
    EvChargerCommandResult? LastCommand,
    IReadOnlyList<EvChargingSessionDto> RecentSessions);

public sealed record EvChargingSessionDto(
    int TransactionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    double EnergyKwh,
    string? StopReason);

public sealed record EvConnectionRejectionDto(
    string ChargePointId,
    string Reason,
    DateTimeOffset AtUtc,
    string? RemoteAddress,
    string RequestedPath);

public static class CarStatusDtoMapper
{
    public static CarStatusResponseDto Map(CarSnapshot car, CarDataConnectionState connection, CarStreamDiagnostics stream, bool isAdmin) => new(
        connection.Status.ToString(),
        ConnectionLabel(connection.Status),
        connection.Message,
        // The sign-in code and the selected descriptors are only useful to the admin who signs in.
        isAdmin ? connection.UserCode : null,
        isAdmin ? connection.VerificationUrl : null,
        connection.SignInExpiresAtUtc,
        connection.ConnectedSinceUtc,
        isAdmin ? car.Vin : null,
        car.StateOfChargePercent,
        car.StateOfChargeAtUtc,
        car.TargetStateOfChargePercent,
        car.ChargingStatus,
        car.IsPlugged,
        car.RemainingRangeKm,
        car.OdometerKm,
        car.LastMessageAtUtc,
        isAdmin ? Dal.BmwCarData.CarDataMessageParser.RequiredDescriptors : [],
        isAdmin ? new CarStreamDiagnosticsDto(stream.Subscription, stream.MessagesReceived, stream.LastMessageAtUtc, stream.LastDescriptors, stream.LastProblem) : null);

    private static string ConnectionLabel(CarDataConnectionStatus status) => status switch
    {
        CarDataConnectionStatus.NotConfigured => "Nicht eingerichtet",
        CarDataConnectionStatus.SignInRequired => "Anmeldung nötig",
        CarDataConnectionStatus.SignInPending => "Anmeldung läuft",
        CarDataConnectionStatus.Connecting => "Verbindet",
        CarDataConnectionStatus.Connected => "Verbunden",
        _ => "Fehler"
    };
}

public sealed record CarStatusResponseDto(
    string ConnectionStatus,
    string ConnectionLabel,
    string? ConnectionMessage,
    string? UserCode,
    string? VerificationUrl,
    DateTimeOffset? SignInExpiresAtUtc,
    DateTimeOffset? ConnectedSinceUtc,
    string? Vin,
    double? StateOfChargePercent,
    DateTimeOffset? StateOfChargeAtUtc,
    double? TargetStateOfChargePercent,
    string? ChargingStatus,
    bool? IsPlugged,
    double? RemainingRangeKm,
    double? OdometerKm,
    DateTimeOffset? LastMessageAtUtc,
    IReadOnlyList<string> RequiredDescriptors,
    CarStreamDiagnosticsDto? Stream)
{
    public CarPollDiagnosticsDto? Poll { get; init; }
}

public sealed record CarPollDiagnosticsDto(DateTimeOffset? LastPollAtUtc, int PollsToday, int DailyLimit, string? LastProblem, string? LastTrigger);

public sealed record CarStreamDiagnosticsDto(
    string? Subscription,
    int MessagesReceived,
    DateTimeOffset? LastMessageAtUtc,
    IReadOnlyList<string> LastDescriptors,
    string? LastProblem);
