using System.Security.Authentication;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Adapter;
using MQTTnet.Formatter;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.BmwCarData;

namespace TibberVictronController.Api.EvCharging;

/// <summary>
/// BMW CarData link (read-only): device-code sign-in, hourly token renewal and the MQTT stream that pushes the car's
/// state of charge. BMW allows one stream per account and the stream password (ID token) expires after an hour, so
/// the connection is renewed shortly before expiry. The last values are persisted because the stream does not
/// replay them after a restart.
/// </summary>
public sealed class CarDataService(
    IServiceScopeFactory scopes,
    CarStatusStore store,
    IUtcClock clock,
    ILogger<CarDataService> logger) : BackgroundService
{
    private const string StreamHost = "customer.streaming-cardata.bmwgroup.com";
    private const int StreamPort = 9000;
    private static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SnapshotPersistInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim wake = new(0);
    private readonly object signInGate = new();
    private CancellationTokenSource? signInCancellation;
    private CancellationToken stopping;
    private CancellationTokenSource? currentRound;
    private DateTimeOffset snapshotPersistedAtUtc = DateTimeOffset.MinValue;
    private bool firstMessageLogged;

    public async Task<CarDataConnectionState> StartSignInAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        if (settings.ClientId is null)
            throw new InvalidOperationException("Zuerst die BMW CarData Client-ID in den Einstellungen eintragen.");
        CarDataDeviceFlow flow;
        await using (var scope = scopes.CreateAsyncScope())
            flow = await scope.ServiceProvider.GetRequiredService<CarDataAuthClient>().StartAsync(settings.ClientId, clock.UtcNow, cancellationToken);

        var state = new CarDataConnectionState(CarDataConnectionStatus.SignInPending,
            "Anmeldung auf der BMW-Seite bestaetigen.", flow.UserCode, flow.VerificationUrl, flow.ExpiresAtUtc);
        store.SetConnection(state);
        CancellationToken token;
        lock (signInGate)
        {
            signInCancellation?.Cancel();
            signInCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            token = signInCancellation.Token;
        }

        _ = Task.Run(() => CompleteSignInAsync(settings.ClientId, flow, token), CancellationToken.None);
        return state;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        lock (signInGate) signInCancellation?.Cancel();
        await SaveAsync(ControllerSettingDefaults.BmwCarDataRefreshTokenKey, null, ControllerSettingSensitivity.Sensitive, cancellationToken);
        await SaveAsync(ControllerSettingDefaults.BmwCarDataGcidKey, null, ControllerSettingSensitivity.Sensitive, cancellationToken);
        store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.SignInRequired, "Von BMW abgemeldet."));
        InterruptRound();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        await RestoreSnapshotAsync(stoppingToken);
        var backoff = TimeSpan.FromSeconds(30);
        while (!stoppingToken.IsCancellationRequested)
        {
            using var round = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Volatile.Write(ref currentRound, round);
            try
            {
                var settings = await ReadSettingsAsync(stoppingToken);
                if (settings.ClientId is null || settings.RefreshToken is null)
                {
                    // A running sign-in shows its own code; it must not be replaced by the idle state.
                    if (store.Connection.Status != CarDataConnectionStatus.SignInPending)
                        store.SetConnection(settings.ClientId is null
                            ? CarDataConnectionState.NotConfigured
                            : new CarDataConnectionState(CarDataConnectionStatus.SignInRequired, "Noch nicht bei BMW angemeldet."));
                    await WaitAsync(IdleCheckInterval, stoppingToken);
                    continue;
                }

                store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.Connecting, "Verbindung zu BMW CarData wird aufgebaut."));
                CarDataTokens tokens;
                await using (var scope = scopes.CreateAsyncScope())
                    tokens = await scope.ServiceProvider.GetRequiredService<CarDataAuthClient>()
                        .RefreshAsync(settings.ClientId, settings.RefreshToken, settings.Gcid, stoppingToken);
                await StoreTokensAsync(tokens, settings, stoppingToken);
                var gcid = tokens.Gcid ?? throw new InvalidOperationException("BMW liefert keine Kundenkennung (GCID).");
                await StreamUntilRenewalAsync(gcid, tokens, settings.Vin, round.Token);
                backoff = TimeSpan.FromSeconds(30);
            }
            catch (CarDataSignInRequiredException exception)
            {
                logger.LogWarning("BMW CarData: {Reason}", exception.Message);
                await SaveAsync(ControllerSettingDefaults.BmwCarDataRefreshTokenKey, null, ControllerSettingSensitivity.Sensitive, stoppingToken);
                store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.SignInRequired, exception.Message));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Sign-in or sign-out ended this round; the next round uses the new state right away.
            }
            catch (Exception exception)
            {
                logger.LogWarning("BMW CarData nicht verbunden: {Reason}", exception.Message);
                store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.Error,
                    $"{exception.Message} Neuer Versuch in {backoff.TotalMinutes:0.#} min."));
                await WaitAsync(backoff, stoppingToken);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
            }
        }
    }

    private async Task StreamUntilRenewalAsync(string gcid, CarDataTokens tokens, string? vin, CancellationToken cancellationToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += eventArguments =>
            HandleMessageAsync(eventArguments.ApplicationMessage.ConvertPayloadToString(), vin);
        client.DisconnectedAsync += eventArguments =>
        {
            disconnected.TrySetResult(eventArguments.ReasonString ?? eventArguments.Reason.ToString());
            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(StreamHost, StreamPort)
            .WithClientId(gcid)
            .WithCredentials(gcid, tokens.IdToken)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTlsOptions(tls => tls.UseTls().WithSslProtocols(SslProtocols.Tls13))
            .Build();
        try
        {
            await client.ConnectAsync(options, cancellationToken);
        }
        catch (MqttConnectingFailedException exception)
        {
            throw new InvalidOperationException(
                $"BMW-Datenstrom lehnt die Verbindung ab ({exception.Message}). CarData Streaming im BMW-Portal abonniert und konfiguriert?", exception);
        }

        var topic = $"{gcid}/{vin ?? "+"}";
        var subscription = await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(topic).Build(), cancellationToken);
        // A refused subscription would leave a connected but silent stream; make it visible and retry later.
        var granted = subscription.Items.FirstOrDefault()?.ResultCode.ToString() ?? "keine Antwort";
        store.UpdateStream(current => current with { Subscription = $"{(vin is null ? "alle Fahrzeuge" : "VIN " + vin)}: {granted}" });
        if (!granted.StartsWith("Granted", StringComparison.Ordinal))
            throw new InvalidOperationException($"BMW lehnt das Abonnement des Datenstroms ab ({granted}). VIN in den Einstellungen eintragen statt 'auto'.");
        firstMessageLogged = false;
        store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.Connected,
            "Mit BMW CarData verbunden. Werte kommen, sobald das Auto Daten sendet.", ConnectedSinceUtc: clock.UtcNow));
        logger.LogInformation("BMW CarData verbunden ({Vehicle}).", vin ?? "alle Fahrzeuge");

        var renewal = tokens.ExpiresIn > TokenRenewalMargin * 2 ? tokens.ExpiresIn - TokenRenewalMargin : tokens.ExpiresIn / 2;
        var finished = await Task.WhenAny(disconnected.Task, Task.Delay(renewal, cancellationToken));
        if (finished == disconnected.Task)
            throw new InvalidOperationException($"BMW-Datenstrom getrennt ({disconnected.Task.Result}).");
        cancellationToken.ThrowIfCancellationRequested();
        // Planned renewal: the next round fetches a fresh ID token and reconnects.
        await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
    }

    private async Task HandleMessageAsync(string json, string? vin)
    {
        var now = clock.UtcNow;
        var (messageVin, descriptors, problem) = CarDataMessageParser.Describe(json);
        if (problem is null && vin is not null && messageVin is not null && !string.Equals(vin, messageVin, StringComparison.OrdinalIgnoreCase))
            problem = "Nachricht fuer ein anderes Fahrzeug.";
        store.UpdateStream(current => current with
        {
            MessagesReceived = current.MessagesReceived + 1,
            LastMessageAtUtc = now,
            LastDescriptors = descriptors,
            LastProblem = problem
        });
        if (!firstMessageLogged)
        {
            firstMessageLogged = true;
            logger.LogInformation("BMW CarData: erste Nachricht empfangen, Datenpunkte {Descriptors}{Problem}.",
                string.Join(", ", descriptors), problem is null ? string.Empty : " – " + problem);
        }

        var updated = false;
        var snapshot = store.UpdateCar(current =>
        {
            var next = CarDataMessageParser.Apply(current, json, vin, now);
            updated = next is not null;
            return next ?? current;
        });
        if (!updated || now - snapshotPersistedAtUtc < SnapshotPersistInterval) return;
        snapshotPersistedAtUtc = now;
        try
        {
            await SaveAsync(ControllerSettingDefaults.BmwCarDataLastSnapshotKey, JsonSerializer.Serialize(snapshot),
                ControllerSettingSensitivity.Normal, stopping);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Letzte Fahrzeugwerte konnten nicht gespeichert werden.");
        }
    }

    private async Task CompleteSignInAsync(string clientId, CarDataDeviceFlow flow, CancellationToken cancellationToken)
    {
        var interval = flow.PollInterval;
        try
        {
            while (clock.UtcNow < flow.ExpiresAtUtc)
            {
                await Task.Delay(interval, cancellationToken);
                CarDataPollResult result;
                await using (var scope = scopes.CreateAsyncScope())
                    result = await scope.ServiceProvider.GetRequiredService<CarDataAuthClient>().PollAsync(clientId, flow, cancellationToken);
                switch (result.Outcome)
                {
                    case CarDataPollOutcome.Pending:
                        continue;
                    case CarDataPollOutcome.SlowDown:
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case CarDataPollOutcome.Completed:
                        await StoreTokensAsync(result.Tokens!, null, cancellationToken);
                        store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.Connecting, "Bei BMW angemeldet. Verbindung wird aufgebaut."));
                        logger.LogInformation("BMW CarData: Anmeldung abgeschlossen.");
                        InterruptRound();
                        return;
                    default:
                        store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.SignInRequired, result.Error));
                        return;
                }
            }

            store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.SignInRequired, "Der Anmeldecode ist abgelaufen. Bitte erneut anmelden."));
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer sign-in, a sign-out or shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning("BMW-Anmeldung fehlgeschlagen: {Reason}", exception.Message);
            store.SetConnection(new CarDataConnectionState(CarDataConnectionStatus.SignInRequired, "BMW-Anmeldung fehlgeschlagen: " + exception.Message));
        }
    }

    private async Task StoreTokensAsync(CarDataTokens tokens, CarDataSettings? previous, CancellationToken cancellationToken)
    {
        // Only the refresh token and the account id are kept; the one-hour ID token lives in memory only.
        if (previous?.RefreshToken != tokens.RefreshToken)
            await SaveAsync(ControllerSettingDefaults.BmwCarDataRefreshTokenKey, tokens.RefreshToken, ControllerSettingSensitivity.Sensitive, cancellationToken);
        if (tokens.Gcid is not null && previous?.Gcid != tokens.Gcid)
            await SaveAsync(ControllerSettingDefaults.BmwCarDataGcidKey, tokens.Gcid, ControllerSettingSensitivity.Sensitive, cancellationToken);
    }

    private async Task RestoreSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var value = (await scope.ServiceProvider.GetRequiredService<IControllerSettingStore>()
                .GetSettingAsync(ControllerSettingDefaults.BmwCarDataLastSnapshotKey, cancellationToken))?.Value;
            if (value is null || value == ControllerSettingDefaults.BmwCarDataNoSnapshot) return;
            if (JsonSerializer.Deserialize<CarSnapshot>(value) is { } snapshot) store.UpdateCar(_ => snapshot);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            logger.LogWarning("Gespeicherte Fahrzeugwerte sind unlesbar und werden ignoriert: {Reason}", exception.Message);
        }
    }

    private async Task<CarDataSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
        async Task<string?> Read(string key) => (await settings.GetSettingAsync(key, cancellationToken))?.Value;
        var clientId = await Read(ControllerSettingDefaults.BmwCarDataClientIdKey);
        var vin = await Read(ControllerSettingDefaults.BmwCarDataVinKey);
        return new CarDataSettings(
            string.IsNullOrWhiteSpace(clientId) || clientId == ControllerSettingDefaults.BmwCarDataNotConfigured ? null : clientId,
            string.IsNullOrWhiteSpace(vin) || vin == ControllerSettingDefaults.BmwCarDataAutoVin ? null : vin.ToUpperInvariant(),
            await Read(ControllerSettingDefaults.BmwCarDataRefreshTokenKey),
            await Read(ControllerSettingDefaults.BmwCarDataGcidKey));
    }

    private async Task SaveAsync(string key, string? value, ControllerSettingSensitivity sensitivity, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IControllerSettingStore>()
            .SaveSettingAsync(new ControllerSetting(key, value, sensitivity, clock.UtcNow), cancellationToken);
    }

    private void InterruptRound()
    {
        try
        {
            Volatile.Read(ref currentRound)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The round has just ended on its own.
        }

        wake.Release();
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // A sign-in or sign-out wakes the loop immediately instead of after the full delay.
        await wake.WaitAsync(delay, cancellationToken);
    }

    private sealed record CarDataSettings(string? ClientId, string? Vin, string? RefreshToken, string? Gcid);
}
