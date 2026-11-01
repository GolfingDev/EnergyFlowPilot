using System.Net.WebSockets;
using System.Text;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Ocpp;

namespace TibberVictronController.Api.EvCharging;

/// <summary>
/// WebSocket transport of the OCPP 1.6J central system. The wallbox connects to
/// ws://&lt;pi&gt;:&lt;OcppListenPort&gt;/ocpp/&lt;chargePointId&gt;. There is no sign-in on this path (the Halo cannot do Entra),
/// so it is bound to its own LAN port, which Tailscale Funnel does not publish, and accepts only the configured id.
/// </summary>
public static class OcppEndpoints
{
    public const string OcppPath = "/ocpp";
    private const string Subprotocol = "ocpp1.6";
    private const int MaximumMessageBytes = 64 * 1024;
    private const string OriginalPathKey = "ocpp.originalPath";
    // The wallbox sends a heartbeat every 60 s; five minutes of silence means the connection is dead.
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMinutes(5);

    public static WebApplicationBuilder AddOcppListener(this WebApplicationBuilder builder)
    {
        var options = EvChargingOptions.Read(builder.Configuration);
        builder.Services.AddSingleton(options);
        if (options.OcppListenPort == 0) return builder;
        // Explicit Kestrel endpoints would replace ASPNETCORE_URLS; appending keeps the existing (nginx) listener.
        var urls = builder.Configuration[WebHostDefaults.ServerUrlsKey];
        if (string.IsNullOrWhiteSpace(urls)) urls = "http://localhost:5000";
        builder.WebHost.UseUrls($"{urls};http://0.0.0.0:{options.OcppListenPort}");
        return builder;
    }

    /// <summary>The OCPP port serves only OCPP, and OCPP is never served through the regular (published) port.</summary>
    public static WebApplication UseOcppPortIsolation(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<EvChargingOptions>();
        if (options.OcppListenPort > 0)
        {
            app.Use((context, next) =>
            {
                var onOcppPort = context.Connection.LocalPort == options.OcppListenPort;
                var isOcppPath = context.Request.Path.StartsWithSegments(OcppPath);
                if (onOcppPort)
                {
                    // The port belongs to OCPP alone, so every path is treated as OCPP: whether the wallbox was given
                    // ws://pi:8887/ocpp/ or just ws://pi:8887/, the last path segment is its id.
                    context.Items[OriginalPathKey] = context.Request.Path.Value;
                    if (!isOcppPath) context.Request.Path = new PathString(OcppPath).Add(context.Request.Path);
                    return next(context);
                }

                if (!isOcppPath) return next(context);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        }

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        return app;
    }

    public static IEndpointRouteBuilder MapOcppEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(OcppPath + "/{**path}", HandleAsync).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }

    private static async Task HandleAsync(HttpContext http, string? path, [AsParameters] OcppConnectionServices services)
    {
        var logger = services.LoggerFactory.CreateLogger("TibberVictronController.Api.EvCharging.Ocpp");
        if (services.Options.OcppListenPort == 0 || http.Connection.LocalPort != services.Options.OcppListenPort)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var requestedPath = http.Items[OriginalPathKey] as string ?? http.Request.Path.Value ?? string.Empty;
        if (!http.WebSockets.IsWebSocketRequest)
        {
            // Visible on the car page: a plain HTTP call usually means a wrong scheme (http:// instead of ws://).
            const string reason = "Keine WebSocket-Verbindung (ws://) angefragt.";
            services.Store.RecordRejected(new EvConnectionRejection("(keine)", reason, services.Clock.UtcNow,
                http.Connection.RemoteIpAddress?.ToString(), Shorten(requestedPath)));
            logger.LogWarning("Anfrage von {Remote} auf {Path} am OCPP-Port abgelehnt: {Reason}", http.Connection.RemoteIpAddress, requestedPath, reason);
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Hier sind nur OCPP-1.6J-WebSocket-Verbindungen moeglich.");
            return;
        }

        // Many wallboxes append their id to the configured URL; the last segment is the id in either case.
        var chargePointId = Uri.UnescapeDataString((path ?? string.Empty).TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty);
        var rejection = await CheckAdmissionAsync(http, chargePointId, services);
        if (rejection is not null)
        {
            var shownId = IsPlausibleId(chargePointId) ? chargePointId : "(ungueltig)";
            services.Store.RecordRejected(new EvConnectionRejection(shownId, rejection, services.Clock.UtcNow,
                http.Connection.RemoteIpAddress?.ToString(), Shorten(requestedPath)));
            logger.LogWarning("Wallbox-Verbindung von {Remote} auf {Path} mit ID {ChargePointId} abgelehnt: {Reason}",
                http.Connection.RemoteIpAddress, requestedPath, shownId, rejection);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var requested = http.WebSockets.WebSocketRequestedProtocols;
        using var socket = await http.WebSockets.AcceptWebSocketAsync(requested.Count > 0 ? Subprotocol : null);
        var generation = services.Store.Connect(chargePointId, services.Clock.UtcNow);
        logger.LogInformation("Wallbox {ChargePointId} verbunden von {Remote}.", chargePointId, http.Connection.RemoteIpAddress);

        using var connectionEnd = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, services.Lifetime.ApplicationStopping);
        var token = connectionEnd.Token;
        var session = new OcppChargePointSession(new OcppConnectionContext(chargePointId, generation,
            (text, ct) => socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask(),
            services.Store, services.Clock, logger));
        var configuration = Task.CompletedTask;
        try
        {
            await using (var scope = services.Scopes.CreateAsyncScope())
                await session.RestoreOpenSessionAsync(scope.ServiceProvider.GetRequiredService<IEvChargingSessionRepository>(), token);
            configuration = ConfigureAsync(session, logger, token);
            await ReceiveLoopAsync(socket, session, services.Scopes, logger, chargePointId, token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            logger.LogInformation("Verbindung zur Wallbox {ChargePointId} beendet: {Reason}", chargePointId, exception.Message);
        }
        finally
        {
            connectionEnd.Cancel();
            session.FailPendingCalls();
            services.Store.Disconnect(generation);
            await configuration;
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Verbindung beendet", closeTimeout.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                {
                    // The connection is going away anyway.
                }
            }
        }
    }

    private static async Task<string?> CheckAdmissionAsync(HttpContext http, string chargePointId, OcppConnectionServices services)
    {
        if (!IsPlausibleId(chargePointId)) return "Die Ladepunkt-ID in der Adresse fehlt oder enthaelt ungueltige Zeichen.";
        await using var scope = services.Scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
        var enabled = await settings.GetSettingAsync(ControllerSettingDefaults.EvOcppEnabledKey, http.RequestAborted);
        if (!bool.TryParse(enabled?.Value, out var isEnabled) || !isEnabled)
            return "Die Wallbox-Anbindung ist in den Einstellungen ausgeschaltet.";
        var configured = (await settings.GetSettingAsync(ControllerSettingDefaults.EvOcppChargePointIdKey, http.RequestAborted))?.Value;
        if (!string.Equals(configured, chargePointId, StringComparison.OrdinalIgnoreCase))
            return "Diese Ladepunkt-ID ist nicht eingetragen. Zum Annehmen in den Einstellungen unter Wallbox (OCPP) uebernehmen.";
        var requested = http.WebSockets.WebSocketRequestedProtocols;
        if (requested.Count > 0 && !requested.Contains(Subprotocol, StringComparer.OrdinalIgnoreCase))
            return $"Die Wallbox verlangt {string.Join(", ", requested)}; unterstuetzt wird nur OCPP 1.6J.";
        return null;
    }

    // The path comes from the network; keep what is stored and shown short.
    private static string Shorten(string path) => path.Length <= 120 ? path : path[..120] + "...";

    private static bool IsPlausibleId(string id) => id.Length is > 0 and <= 64 &&
        !string.Equals(id, ControllerSettingDefaults.EvOcppChargePointNotConfigured, StringComparison.OrdinalIgnoreCase) &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private static async Task ConfigureAsync(OcppChargePointSession session, ILogger logger, CancellationToken token)
    {
        try
        {
            await session.ConfigureMeteringAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Connection closed before the wallbox answered.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Messwert-Einstellung der Wallbox fehlgeschlagen.");
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, OcppChargePointSession session, IServiceScopeFactory scopes,
        ILogger logger, string chargePointId, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            message.SetLength(0);
            ValueWebSocketReceiveResult result;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                silence.CancelAfter(ReceiveTimeout);
                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), silence.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        if (message.Length + result.Count > MaximumMessageBytes)
                        {
                            logger.LogWarning("Wallbox {ChargePointId} sendet zu grosse Nachricht; Verbindung wird getrennt.", chargePointId);
                            await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Nachricht zu gross", token);
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    logger.LogWarning("Wallbox {ChargePointId} sendet seit {Minutes} Minuten nichts; Verbindung wird getrennt.",
                        chargePointId, ReceiveTimeout.TotalMinutes);
                    return;
                }
            }

            if (result.MessageType != WebSocketMessageType.Text) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            await using var scope = scopes.CreateAsyncScope();
            await session.HandleTextAsync(text, scope.ServiceProvider.GetRequiredService<IEvChargingSessionRepository>(), token);
        }
    }
}

/// <summary>Parameter object for the OCPP endpoint, resolved by minimal-API injection.</summary>
public sealed record OcppConnectionServices(
    EvChargingOptions Options,
    EvChargerStatusStore Store,
    IServiceScopeFactory Scopes,
    IUtcClock Clock,
    ILoggerFactory LoggerFactory,
    IHostApplicationLifetime Lifetime);
