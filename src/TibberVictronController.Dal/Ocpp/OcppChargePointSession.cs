using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Ocpp;

/// <summary>Everything one wallbox connection needs; the transport (WebSocket) stays outside.</summary>
public sealed record OcppConnectionContext(
    string ChargePointId,
    long Generation,
    Func<string, CancellationToken, Task> SendText,
    EvChargerStatusStore Store,
    IUtcClock Clock,
    ILogger Logger);

/// <summary>
/// OCPP 1.6J central-system logic for one connected wallbox (stage 1, read-only). Charging is never limited here:
/// every id tag is accepted and no charging profile is sent. The only writes are metering settings, so the
/// dashboard gets power values at all.
/// </summary>
public sealed class OcppChargePointSession
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    // Live values go to the snapshot every time; the database only needs the meter once a minute (SD card wear).
    private static readonly TimeSpan MeterPersistInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DerivedPowerMaximumGap = TimeSpan.FromMinutes(5);
    private const string DesiredSampleIntervalSeconds = "10";
    private const string PowerMeasurand = "Power.Active.Import";
    private const string EnergyMeasurand = "Energy.Active.Import.Register";
    // Tried in order: some firmware rejects the whole list when one measurand is unsupported.
    private static readonly string[] SampledDataCandidates =
    [
        EnergyMeasurand + "," + PowerMeasurand + ",Current.Import",
        EnergyMeasurand + "," + PowerMeasurand
    ];
    private static readonly string[] HiddenConfigurationParts = ["passw", "secret", "token", "authorizationkey", "apikey", "psk"];
    private static readonly object AcceptedIdTag = new { status = "Accepted" };

    private readonly OcppConnectionContext context;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<OcppFrame>> pendingCalls = new();
    private readonly TaskCompletionSource firstCallHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private int nextMessageId;
    private ActiveTransaction? active;
    private DateTimeOffset? meterPersistedAtUtc;
    private (double Wh, DateTimeOffset At)? previousEnergy;

    public OcppChargePointSession(OcppConnectionContext context)
    {
        this.context = context;
    }

    /// <summary>After a controller restart the wallbox may still be charging; continue its open session.</summary>
    public async Task RestoreOpenSessionAsync(IEvChargingSessionRepository sessions, CancellationToken cancellationToken)
    {
        var open = (await sessions.GetRecentAsync(5, cancellationToken))
            .FirstOrDefault(session => session.IsActive && session.ChargePointId == context.ChargePointId);
        if (open is null) return;
        active = new ActiveTransaction(open.TransactionId, open.MeterStartWh);
        Update(snapshot => snapshot with
        {
            ActiveTransactionId = open.TransactionId,
            SessionStartedAtUtc = open.StartedAtUtc,
            SessionEnergyKwh = open.EnergyKwh,
            MeterWh = open.LastMeterWh
        });
    }

    /// <summary>Handles one received text message; CALLs are answered, answers complete our own pending calls.</summary>
    public async Task HandleTextAsync(string text, IEvChargingSessionRepository sessions, CancellationToken cancellationToken)
    {
        var now = context.Clock.UtcNow;
        Update(snapshot => snapshot with { LastMessageAtUtc = now });
        OcppFrame frame;
        try
        {
            frame = OcppFrame.Parse(text);
        }
        catch (FormatException exception)
        {
            context.Logger.LogWarning("Ungueltige OCPP-Nachricht von {ChargePointId}: {Reason}", context.ChargePointId, exception.Message);
            if (OcppFrame.TryReadUniqueId(text) is { } brokenId)
                await SendAsync(OcppFrame.Error(brokenId, "FormationViolation", exception.Message), cancellationToken);
            return;
        }

        if (frame.Type != OcppMessageType.Call)
        {
            if (pendingCalls.TryRemove(frame.UniqueId, out var waiting)) waiting.TrySetResult(frame);
            return;
        }

        string reply;
        try
        {
            var result = await HandleCallAsync(frame.Action!, frame.Payload, sessions, now, cancellationToken);
            reply = result is null
                ? OcppFrame.Error(frame.UniqueId, "NotImplemented", $"Die Aktion '{frame.Action}' wird nicht unterstuetzt.")
                : OcppFrame.Result(frame.UniqueId, result);
        }
        catch (OcppPayloadException exception)
        {
            context.Logger.LogWarning("OCPP-{Action} von {ChargePointId} abgelehnt: {Reason}", frame.Action, context.ChargePointId, exception.Message);
            reply = OcppFrame.Error(frame.UniqueId, "FormationViolation", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // InternalError makes the wallbox retry transaction messages later instead of losing them.
            context.Logger.LogError(exception, "OCPP-{Action} von {ChargePointId} konnte nicht verarbeitet werden.", frame.Action, context.ChargePointId);
            reply = OcppFrame.Error(frame.UniqueId, "InternalError", "Die Nachricht konnte nicht verarbeitet werden.");
        }

        await SendAsync(reply, cancellationToken);
        firstCallHandled.TrySetResult();
    }

    /// <summary>
    /// Reads the wallbox configuration and makes sure it reports power and energy at a useful interval. Runs once per
    /// connection, after the wallbox has sent its first message (a booting charger must not get requests earlier).
    /// </summary>
    public async Task ConfigureMeteringAsync(CancellationToken cancellationToken)
    {
        await firstCallHandled.Task.WaitAsync(cancellationToken);
        try
        {
            var response = await CallAsync("GetConfiguration", new { }, cancellationToken);
            if (response.Type == OcppMessageType.CallError)
            {
                SetNote($"Die Wallbox liefert ihre Konfiguration nicht ({response.ErrorCode}).");
                return;
            }

            var configuration = ReadConfiguration(response.Payload);
            Update(snapshot => snapshot with
            {
                Configuration = configuration
                    .Where(entry => !HiddenConfigurationParts.Any(part => entry.Key.Contains(part, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(entry => entry.Key, entry => entry.Value.Value, StringComparer.Ordinal)
            });

            var notes = new List<string>();
            var interval = configuration.GetValueOrDefault("MeterValueSampleInterval");
            if (!int.TryParse(interval.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 60)
                notes.Add(await ChangeAsync("MeterValueSampleInterval", DesiredSampleIntervalSeconds, interval.ReadOnly, cancellationToken));

            var sampled = configuration.GetValueOrDefault("MeterValuesSampledData");
            if (sampled.Value?.Contains(PowerMeasurand, StringComparison.OrdinalIgnoreCase) != true)
            {
                foreach (var candidate in SampledDataCandidates)
                {
                    var note = await ChangeAsync("MeterValuesSampledData", candidate, sampled.ReadOnly, cancellationToken);
                    notes.Add(note);
                    if (note.StartsWith("Gesetzt", StringComparison.Ordinal) || sampled.ReadOnly) break;
                }
            }

            SetNote(notes.Count == 0 ? "Messwerte der Wallbox sind bereits passend eingestellt." : string.Join(" ", notes));
        }
        catch (TimeoutException)
        {
            SetNote("Die Wallbox beantwortet die Konfigurationsabfrage nicht.");
        }

        try
        {
            // After a controller restart the wallbox only reports on changes; ask once for the current state.
            await CallAsync("TriggerMessage", new { requestedMessage = "StatusNotification" }, cancellationToken);
            await CallAsync("TriggerMessage", new { requestedMessage = "MeterValues", connectorId = 1 }, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Optional: the state then arrives with the next change at the wallbox.
        }
    }

    // One fixed id: a new limit replaces the previous one, and clearing removes exactly the controller's profile.
    public const int ControllerProfileId = 70001;

    /// <summary>
    /// Sets a TxDefaultProfile with a limit in amps that is valid only for the given time. Tried on connector 1 and,
    /// if the wallbox refuses, on connector 0 (some firmware accepts default profiles only there).
    /// </summary>
    public async Task<EvChargerCommandResult> SetCurrentLimitAsync(double amps, TimeSpan validity, CancellationToken cancellationToken)
    {
        var now = context.Clock.UtcNow;
        var validTo = now + validity;
        object Profile(int connectorId) => new
        {
            connectorId,
            csChargingProfiles = new
            {
                chargingProfileId = ControllerProfileId,
                stackLevel = 1,
                chargingProfilePurpose = "TxDefaultProfile",
                chargingProfileKind = "Absolute",
                validFrom = OcppFrame.Timestamp(now),
                validTo = OcppFrame.Timestamp(validTo),
                chargingSchedule = new
                {
                    duration = (int)validity.TotalSeconds,
                    startSchedule = OcppFrame.Timestamp(now),
                    chargingRateUnit = "A",
                    chargingSchedulePeriod = new[] { new { startPeriod = 0, limit = Math.Round(amps, 1), numberPhases = 3 } }
                }
            }
        };

        var attempts = new List<string>();
        foreach (var connectorId in new[] { 1, 0 })
        {
            var status = await StatusOfAsync("SetChargingProfile", Profile(connectorId), cancellationToken);
            attempts.Add($"Anschluss {connectorId}: {status}");
            if (status == "Accepted")
                return Record(new EvChargerCommandResult(true, status,
                    $"Ladestrom auf {amps:0.#} A begrenzt bis {validTo:HH:mm} UTC ({string.Join(", ", attempts)}).", now, validTo));
        }

        return Record(new EvChargerCommandResult(false, attempts.Count > 0 ? attempts[^1].Split(": ")[1] : "Unbekannt",
            $"Wallbox lehnt die Begrenzung auf {amps:0.#} A ab ({string.Join(", ", attempts)}).", now, null));
    }

    public async Task<EvChargerCommandResult> ClearCurrentLimitAsync(CancellationToken cancellationToken)
    {
        var now = context.Clock.UtcNow;
        var status = await StatusOfAsync("ClearChargingProfile", new { id = ControllerProfileId }, cancellationToken);
        // "Unknown" means there was no such profile any more, which is the wanted state too.
        var accepted = status is "Accepted" or "Unknown";
        return Record(new EvChargerCommandResult(accepted, status,
            accepted ? "Begrenzung aufgehoben; die Wallbox lädt wieder nach ihrer eigenen Einstellung." : $"Wallbox hebt die Begrenzung nicht auf ({status}).",
            now, null));
    }

    private async Task<string> StatusOfAsync(string action, object payload, CancellationToken cancellationToken)
    {
        try
        {
            var response = await CallAsync(action, payload, cancellationToken);
            return response.Type == OcppMessageType.CallResult
                ? OcppPayload.OptionalText(response.Payload, "status") ?? "OhneStatus"
                : response.ErrorCode ?? "Fehler";
        }
        catch (TimeoutException)
        {
            return "KeineAntwort";
        }
    }

    private EvChargerCommandResult Record(EvChargerCommandResult result)
    {
        Update(snapshot => snapshot with { LastCommand = result });
        context.Logger.LogInformation("Wallbox {ChargePointId}: {Description}", context.ChargePointId, result.Description);
        return result;
    }

    public void FailPendingCalls()
    {
        foreach (var call in pendingCalls.Values) call.TrySetCanceled();
    }

    private async Task<object?> HandleCallAsync(string action, JsonElement payload, IEvChargingSessionRepository sessions,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case "BootNotification":
                return Boot(payload, now);
            case "Heartbeat":
                return new { currentTime = OcppFrame.Timestamp(now) };
            case "StatusNotification":
                Status(payload, now);
                return new { };
            case "Authorize":
                return new { idTagInfo = AcceptedIdTag };
            case "StartTransaction":
                return await StartTransactionAsync(payload, sessions, now, cancellationToken);
            case "StopTransaction":
                return await StopTransactionAsync(payload, sessions, now, cancellationToken);
            case "MeterValues":
                await MeterValuesAsync(payload, sessions, now, cancellationToken);
                return new { };
            case "DataTransfer":
                return new { status = "UnknownVendorId" };
            case "FirmwareStatusNotification":
            case "DiagnosticsStatusNotification":
                return new { };
            default:
                return null;
        }
    }

    private object Boot(JsonElement payload, DateTimeOffset now)
    {
        var vendor = OcppPayload.Text(payload, "chargePointVendor");
        var model = OcppPayload.Text(payload, "chargePointModel");
        var firmware = OcppPayload.OptionalText(payload, "firmwareVersion");
        Update(snapshot => snapshot with { Vendor = vendor, Model = model, FirmwareVersion = firmware });
        context.Logger.LogInformation("Wallbox {ChargePointId} gestartet: {Vendor} {Model}, Firmware {Firmware}.",
            context.ChargePointId, vendor, model, firmware ?? "unbekannt");
        return new { status = "Accepted", currentTime = OcppFrame.Timestamp(now), interval = (int)HeartbeatInterval.TotalSeconds };
    }

    private void Status(JsonElement payload, DateTimeOffset now)
    {
        var connectorId = OcppPayload.Int(payload, "connectorId");
        var status = OcppPayload.Text(payload, "status");
        var errorCode = OcppPayload.Text(payload, "errorCode");
        var error = errorCode == "NoError" ? null : errorCode;
        if (connectorId == 0)
        {
            // Connector 0 is the wallbox as a whole; only its errors matter for the single-connector Halo.
            if (error is not null) Update(snapshot => snapshot with { ErrorCode = error, StatusAtUtc = now });
            return;
        }

        var parsed = Enum.TryParse<EvConnectorStatus>(status, ignoreCase: true, out var value) ? value : EvConnectorStatus.Unknown;
        Update(snapshot => snapshot with
        {
            ConnectorStatus = parsed,
            ErrorCode = error,
            StatusAtUtc = now,
            // Without an active charge there is no load, even if the last meter value said otherwise.
            PowerWatts = parsed == EvConnectorStatus.Charging ? snapshot.PowerWatts : 0,
            PowerIsDerived = parsed == EvConnectorStatus.Charging && snapshot.PowerIsDerived
        });
    }

    private async Task<object> StartTransactionAsync(JsonElement payload, IEvChargingSessionRepository sessions,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var start = new EvChargingSessionStart(
            context.ChargePointId,
            OcppPayload.Int(payload, "connectorId"),
            Truncate(OcppPayload.Text(payload, "idTag"), 64),
            OcppPayload.PlausibleTimestamp(payload, "timestamp", now),
            OcppPayload.Number(payload, "meterStart"));
        var session = await sessions.StartAsync(start, cancellationToken);
        active = new ActiveTransaction(session.TransactionId, session.MeterStartWh);
        previousEnergy = (session.MeterStartWh, session.StartedAtUtc);
        meterPersistedAtUtc = now;
        Update(snapshot => snapshot with
        {
            ActiveTransactionId = session.TransactionId,
            SessionStartedAtUtc = session.StartedAtUtc,
            SessionEnergyKwh = session.EnergyKwh,
            MeterWh = session.MeterStartWh
        });
        context.Logger.LogInformation("Ladevorgang {TransactionId} an {ChargePointId} begonnen (Zaehler {MeterWh} Wh).",
            session.TransactionId, context.ChargePointId, session.MeterStartWh);
        return new { transactionId = session.TransactionId, idTagInfo = AcceptedIdTag };
    }

    private async Task<object> StopTransactionAsync(JsonElement payload, IEvChargingSessionRepository sessions,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transactionId = OcppPayload.Int(payload, "transactionId");
        var meterStop = OcppPayload.Number(payload, "meterStop");
        var session = await sessions.StopAsync(transactionId, meterStop, OcppPayload.PlausibleTimestamp(payload, "timestamp", now),
            OcppPayload.OptionalText(payload, "reason") ?? "Local", cancellationToken);
        if (active?.TransactionId == transactionId) active = null;
        Update(snapshot => snapshot.ActiveTransactionId is not null && snapshot.ActiveTransactionId != transactionId
            ? snapshot
            : snapshot with
            {
                ActiveTransactionId = null,
                SessionEnergyKwh = session?.EnergyKwh ?? snapshot.SessionEnergyKwh,
                MeterWh = meterStop,
                PowerWatts = 0,
                PowerIsDerived = false
            });
        if (session is null)
            context.Logger.LogWarning("Wallbox {ChargePointId} beendet unbekannten Ladevorgang {TransactionId}.", context.ChargePointId, transactionId);
        else
            context.Logger.LogInformation("Ladevorgang {TransactionId} beendet: {EnergyKwh:0.00} kWh.", transactionId, session.EnergyKwh);
        // The stop is always confirmed; otherwise the wallbox would resend it forever.
        return new { idTagInfo = AcceptedIdTag };
    }

    private async Task MeterValuesAsync(JsonElement payload, IEvChargingSessionRepository sessions,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transactionId = OcppPayload.OptionalInt(payload, "transactionId");
        if (!payload.TryGetProperty("meterValue", out var meterValues) || meterValues.ValueKind != JsonValueKind.Array)
            throw new OcppPayloadException("Das Pflichtfeld 'meterValue' fehlt.");
        var reading = LatestReading(meterValues, now);
        if (reading is null) return;

        if (transactionId is { } id && active?.TransactionId != id &&
            await sessions.GetAsync(id, cancellationToken) is { IsActive: true } known)
            active = new ActiveTransaction(known.TransactionId, known.MeterStartWh);

        var power = reading.PowerWatts;
        var derived = false;
        if (power is null && reading.EnergyWh is { } energy && previousEnergy is { } previous &&
            reading.At > previous.At && reading.At - previous.At <= DerivedPowerMaximumGap)
        {
            // Firmware without the power measurand: average power since the previous meter reading.
            power = Math.Max(0, (energy - previous.Wh) / (reading.At - previous.At).TotalHours);
            derived = true;
        }

        if (reading.EnergyWh is { } latest) previousEnergy = (latest, reading.At);
        var current = active;
        double? sessionEnergy = current is not null && reading.EnergyWh is { } meter ? Math.Max(0, (meter - current.MeterStartWh) / 1000d) : null;

        if (current is not null && reading.EnergyWh is { } persisted &&
            (meterPersistedAtUtc is null || now - meterPersistedAtUtc >= MeterPersistInterval))
        {
            await sessions.UpdateMeterAsync(current.TransactionId, persisted, reading.At, cancellationToken);
            meterPersistedAtUtc = now;
        }

        Update(snapshot => snapshot with
        {
            ActiveTransactionId = current?.TransactionId ?? snapshot.ActiveTransactionId,
            MeterWh = reading.EnergyWh ?? snapshot.MeterWh,
            SessionEnergyKwh = sessionEnergy ?? snapshot.SessionEnergyKwh,
            PowerWatts = power ?? snapshot.PowerWatts,
            PowerIsDerived = power is not null ? derived : snapshot.PowerIsDerived,
            MeasuredAtUtc = reading.At,
            CurrentL1Amps = reading.L1Amps ?? snapshot.CurrentL1Amps,
            CurrentL2Amps = reading.L2Amps ?? snapshot.CurrentL2Amps,
            CurrentL3Amps = reading.L3Amps ?? snapshot.CurrentL3Amps
        });
    }

    /// <summary>Reduces a MeterValues message to the newest sample: total energy, total power and phase currents.</summary>
    internal static MeterReading? LatestReading(JsonElement meterValues, DateTimeOffset now)
    {
        MeterReading? latest = null;
        foreach (var meterValue in meterValues.EnumerateArray())
        {
            if (meterValue.ValueKind != JsonValueKind.Object ||
                !meterValue.TryGetProperty("sampledValue", out var sampled) || sampled.ValueKind != JsonValueKind.Array) continue;
            double? energy = null, power = null, phasePower = null, l1 = null, l2 = null, l3 = null;
            foreach (var sample in sampled.EnumerateArray())
            {
                if (sample.ValueKind != JsonValueKind.Object ||
                    !double.TryParse(OcppPayload.OptionalText(sample, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                // Values measured inside the car (location "EV", e.g. its SoC) are not wallbox readings.
                if (OcppPayload.OptionalText(sample, "location") == "EV") continue;
                var measurand = OcppPayload.OptionalText(sample, "measurand") ?? EnergyMeasurand;
                var unit = OcppPayload.OptionalText(sample, "unit");
                var phase = OcppPayload.OptionalText(sample, "phase");
                var factor = unit is "kWh" or "kW" ? 1000d : 1d;
                switch (measurand)
                {
                    case EnergyMeasurand when phase is null:
                        energy = value * factor;
                        break;
                    case PowerMeasurand when phase is null:
                        power = value * factor;
                        break;
                    case PowerMeasurand:
                        phasePower = (phasePower ?? 0) + value * factor;
                        break;
                    case "Current.Import":
                        switch (phase)
                        {
                            case "L1" or "L1-N": l1 = value; break;
                            case "L2" or "L2-N": l2 = value; break;
                            case "L3" or "L3-N": l3 = value; break;
                        }
                        break;
                }
            }

            if (energy is null && power is null && phasePower is null && l1 is null && l2 is null && l3 is null) continue;
            var at = OcppPayload.PlausibleTimestamp(meterValue, "timestamp", now);
            if (latest is null || at >= latest.At) latest = new MeterReading(at, energy, power ?? phasePower, l1, l2, l3);
        }

        return latest;
    }

    private static Dictionary<string, (string? Value, bool ReadOnly)> ReadConfiguration(JsonElement payload)
    {
        var result = new Dictionary<string, (string? Value, bool ReadOnly)>(StringComparer.Ordinal);
        if (!payload.TryGetProperty("configurationKey", out var keys) || keys.ValueKind != JsonValueKind.Array) return result;
        foreach (var entry in keys.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || OcppPayload.OptionalText(entry, "key") is not { } key) continue;
            var readOnly = entry.TryGetProperty("readonly", out var flag) && flag.ValueKind == JsonValueKind.True;
            result[key] = (OcppPayload.OptionalText(entry, "value"), readOnly);
        }

        return result;
    }

    private async Task<string> ChangeAsync(string key, string value, bool readOnly, CancellationToken cancellationToken)
    {
        if (readOnly) return $"{key} ist an der Wallbox schreibgeschuetzt.";
        var response = await CallAsync("ChangeConfiguration", new { key, value }, cancellationToken);
        var status = response.Type == OcppMessageType.CallResult ? OcppPayload.OptionalText(response.Payload, "status") : response.ErrorCode;
        return status is "Accepted" or "RebootRequired"
            ? $"Gesetzt: {key}={value}" + (status == "RebootRequired" ? " (wirkt nach Neustart der Wallbox)." : ".")
            : $"Wallbox lehnt {key}={value} ab ({status ?? "ohne Angabe"}).";
    }

    private async Task<OcppFrame> CallAsync(string action, object payload, CancellationToken cancellationToken)
    {
        var id = "efp-" + Interlocked.Increment(ref nextMessageId).ToString(CultureInfo.InvariantCulture);
        var answer = new TaskCompletionSource<OcppFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingCalls[id] = answer;
        try
        {
            await SendAsync(OcppFrame.Call(id, action, payload), cancellationToken);
            return await answer.Task.WaitAsync(CallTimeout, cancellationToken);
        }
        finally
        {
            pendingCalls.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        // A WebSocket allows one send at a time; answers and our own requests can overlap.
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            await context.SendText(text, cancellationToken);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void SetNote(string note)
    {
        Update(snapshot => snapshot with { ConfigurationNote = note });
        context.Logger.LogInformation("Wallbox {ChargePointId}: {Note}", context.ChargePointId, note);
    }

    private void Update(Func<EvChargerSnapshot, EvChargerSnapshot> change) => context.Store.Update(context.Generation, change);

    private static string Truncate(string value, int maximumLength) => value.Length <= maximumLength ? value : value[..maximumLength];

    private sealed record ActiveTransaction(int TransactionId, double MeterStartWh);

    internal sealed record MeterReading(DateTimeOffset At, double? EnergyWh, double? PowerWatts, double? L1Amps, double? L2Amps, double? L3Amps);
}
