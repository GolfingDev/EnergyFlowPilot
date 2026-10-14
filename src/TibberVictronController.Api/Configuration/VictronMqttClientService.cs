using System.Text.Json;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TibberVictronController.Api.Dashboard;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Keeps one Victron MQTT connection alive for telemetry and control writes.
/// </summary>
public sealed class VictronMqttClientService : BackgroundService, IVictronMqttControlClient
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoopDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumVictronKeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LiveSampleCleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SupplementaryPvMaxAge = TimeSpan.FromMinutes(5);
    private const int DefaultLiveSampleRetentionDays = 14;
    private const string SuppressedKeepAlivePayload = """{"keepalive-options":["suppress-republish"]}""";

    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<VictronMqttClientService> logger;
    private readonly VictronMqttRuntimeStatus runtimeStatus;
    private readonly DecisionCalculationTrigger calculationTrigger;
    private readonly SignificantTelemetryChangeDetector telemetryChangeDetector;
    private readonly ConcurrentDictionary<string, decimal> latestValuesByTopic = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private IMqttClient? mqttClient;
    private VictronMqttSettings? activeSettings;
    private DateTimeOffset nextLiveSampleCleanupAtUtc = DateTimeOffset.MinValue;
    private long receivedTelemetryMessageCount;

    public VictronMqttClientService(
        IServiceProvider serviceProvider,
        ILogger<VictronMqttClientService> logger,
        VictronMqttRuntimeStatus runtimeStatus,
        DecisionCalculationTrigger calculationTrigger,
        SignificantTelemetryChangeDetector telemetryChangeDetector)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
        this.runtimeStatus = runtimeStatus;
        this.calculationTrigger = calculationTrigger;
        this.telemetryChangeDetector = telemetryChangeDetector;
    }

    public async Task PublishValueAsync(string topic, int value, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var client = mqttClient;
        var settings = activeSettings;
        if (client is null || settings is null || !client.IsConnected)
        {
            throw new InvalidOperationException("Victron MQTT ist nicht verbunden.");
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.Serialize(new { value }))
            .Build();

        try
        {
            await client.PublishAsync(message, cancellationToken);
            logger.LogDebug("Victron-MQTT-Wert veroeffentlicht. Topic={Topic}, Value={Value}", topic, value);
        }
        catch (Exception exception)
        {
            await SaveControlPublishFailureAsync(settings, topic, value, exception, cancellationToken);
            await DisconnectClientAsync(CancellationToken.None);
            throw;
        }
    }

    public bool TryGetLatestValue(string topic, out decimal value)
    {
        return latestValuesByTopic.TryGetValue(topic, out value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                runtimeStatus.MarkStarting();
                await RunConnectedLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                runtimeStatus.MarkFailed($"Victron MQTT konnte nicht betrieben werden: {exception.Message}");
                logger.LogError(exception, "Victron MQTT konnte nicht initialisiert oder betrieben werden.");
                await DisconnectClientAsync(CancellationToken.None);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }

        runtimeStatus.MarkStopped();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        runtimeStatus.MarkStopped();
        await DisconnectClientAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task RunConnectedLoopAsync(CancellationToken stoppingToken)
    {
        await EnsureConnectedAsync(stoppingToken);

        var initialFullPublishRequested = false;
        var nextKeepAliveAtUtc = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = activeSettings ?? throw new InvalidOperationException("Victron MQTT ist nicht konfiguriert.");
            var keepAliveInterval = TimeSpan.FromSeconds(settings.KeepAliveSeconds);
            if (keepAliveInterval < MinimumVictronKeepAliveInterval)
            {
                keepAliveInterval = MinimumVictronKeepAliveInterval;
            }

            var utcNow = DateTimeOffset.UtcNow;

            if (mqttClient is null || !mqttClient.IsConnected)
            {
                throw new InvalidOperationException("Victron MQTT-Verbindung wurde unterbrochen und wird neu aufgebaut.");
            }

            if (utcNow >= nextKeepAliveAtUtc)
            {
                await PublishKeepAliveAsync(settings, suppressRepublish: initialFullPublishRequested, stoppingToken);
                initialFullPublishRequested = true;
                nextKeepAliveAtUtc = utcNow.Add(keepAliveInterval);
            }

            if (!SettingsMatch(settings, await ReadSettingsAsync(stoppingToken)))
            {
                runtimeStatus.MarkFailed("Victron MQTT-Konfiguration wurde geaendert. Die Verbindung wird neu aufgebaut.");
                throw new InvalidOperationException("Victron MQTT-Konfiguration wurde geaendert. Die Verbindung wird neu aufgebaut.");
            }

            await Task.Delay(LoopDelay, stoppingToken);
        }
    }

    public static string CreateKeepAlivePayload(bool suppressRepublish)
    {
        return suppressRepublish ? SuppressedKeepAlivePayload : string.Empty;
    }

    public static bool RequiresReconnect(VictronMqttSettings left, VictronMqttSettings right)
    {
        return !SettingsMatch(left, right);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await connectionLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await ReadSettingsAsync(cancellationToken);
            if (mqttClient is not null &&
                mqttClient.IsConnected &&
                activeSettings is not null &&
                SettingsMatch(activeSettings, settings))
            {
                return;
            }

            await DisconnectClientCoreAsync(cancellationToken);

            var topics = VictronMqttTopicFactory.Create(settings);
            var snapshotStore = serviceProvider.GetRequiredService<MqttTelemetrySnapshotStore>();
            snapshotStore.Clear(preserveLatestValues: true);
            latestValuesByTopic.Clear();
            Interlocked.Exchange(ref receivedTelemetryMessageCount, 0);

            var client = new MqttClientFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += async eventArguments =>
            {
                await HandleMessageAsync(eventArguments, topics, snapshotStore);
            };
            client.DisconnectedAsync += eventArguments =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    var reason = eventArguments.ReasonString ?? eventArguments.Reason.ToString();
                    runtimeStatus.MarkFailed($"Victron MQTT-Verbindung wurde unterbrochen: {reason}");
                    logger.LogWarning(
                        eventArguments.Exception,
                        "Victron MQTT-Verbindung wurde unterbrochen. Reason={Reason}, ReasonString={ReasonString}. Ein neuer Verbindungsaufbau wird vorbereitet.",
                        eventArguments.Reason,
                        eventArguments.ReasonString);
                }

                return Task.CompletedTask;
            };

            var clientId = CreateClientId(settings);
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.Host, settings.Port)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
                .WithClientId(clientId)
                .Build();
            var subscribeOptions = new MqttClientSubscribeOptions
            {
                TopicFilters = topics.ReadTopics
                    .Select(topic => new MqttTopicFilterBuilder()
                        .WithTopic(topic)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                        .Build())
                    .ToList()
            };

            await client.ConnectAsync(options, cancellationToken);
            await client.SubscribeAsync(subscribeOptions, cancellationToken);

            mqttClient = client;
            activeSettings = settings;
            runtimeStatus.MarkConnected();

            logger.LogInformation(
                "Victron MQTT verbunden. ClientId={ClientId}, PortalId={PortalId}, Host={Host}, Port={Port}, Topics={TopicCount}",
                clientId,
                settings.PortalId,
                settings.Host,
                settings.Port,
                subscribeOptions.TopicFilters.Count);
        }
        catch (Exception exception)
        {
            await SaveConnectionFailureAsync(exception, cancellationToken);
            throw;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task HandleMessageAsync(
        MqttApplicationMessageReceivedEventArgs eventArguments,
        VictronMqttTopics topics,
        MqttTelemetrySnapshotStore snapshotStore)
    {
        var payload = eventArguments.ApplicationMessage.ConvertPayloadToString();

        if (!VictronMqttPayloadParser.TryParseDecimal(payload, out var value))
        {
            logger.LogWarning("Victron MQTT Payload konnte nicht geparst werden. Topic={Topic}", eventArguments.ApplicationMessage.Topic);
            return;
        }

        var measuredAtUtc = DateTimeOffset.UtcNow;
        latestValuesByTopic[eventArguments.ApplicationMessage.Topic] = value;
        Interlocked.Increment(ref receivedTelemetryMessageCount);
        runtimeStatus.MarkMessageReceived(measuredAtUtc);

        var signalKind = GetTelemetrySignalKind(eventArguments.ApplicationMessage.Topic, topics);
        var shouldPersistConsumptionSample = ApplyTelemetryValue(
            eventArguments.ApplicationMessage.Topic,
            value,
            measuredAtUtc,
            topics,
            snapshotStore,
            latestValuesByTopic,
            out var houseConsumptionWatts);

        if (signalKind is not null &&
            telemetryChangeDetector.ShouldTrigger(eventArguments.ApplicationMessage.Topic, value, signalKind.Value))
        {
            calculationTrigger.Signal();
            logger.LogDebug(
                "Decision-Neuberechnung wegen signifikanter Telemetrieaenderung signalisiert. Topic={Topic}, Value={Value}",
                eventArguments.ApplicationMessage.Topic,
                value);
        }

        if (shouldPersistConsumptionSample)
        {
            await PersistLiveConsumptionSampleAsync(snapshotStore, houseConsumptionWatts, measuredAtUtc);
        }

        var dashboardLiveUpdatePublisher = serviceProvider.GetService<IDashboardLiveUpdatePublisher>();
        if (dashboardLiveUpdatePublisher is not null)
        {
            await dashboardLiveUpdatePublisher.PublishTelemetryAsync(snapshotStore.GetSnapshot());
        }
    }

    private static bool ApplyTelemetryValue(
        string topic,
        decimal value,
        DateTimeOffset measuredAtUtc,
        VictronMqttTopics topics,
        MqttTelemetrySnapshotStore snapshotStore,
        IReadOnlyDictionary<string, decimal> latestValuesByTopic,
        out decimal houseConsumptionWatts)
    {
        houseConsumptionWatts = 0m;

        if (string.Equals(topic, topics.GridPowerTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateGridPower(value, measuredAtUtc);
            return false;
        }

        if (string.Equals(topic, topics.BatterySocTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateBatterySoc(value, measuredAtUtc);
            return false;
        }

        if (string.Equals(topic, topics.BatteryPowerTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateBatteryPower(value, measuredAtUtc);
            return false;
        }

        if (topics.HouseConsumptionTopics.Contains(topic, StringComparer.Ordinal))
        {
            // Phases that have not reported yet count as 0 W so a single configured topic keeps its previous behavior.
            houseConsumptionWatts = topics.HouseConsumptionTopics.Sum(houseTopic =>
                latestValuesByTopic.TryGetValue(houseTopic, out var phaseWatts) ? phaseWatts : 0m);
            snapshotStore.UpdateHouseConsumption(houseConsumptionWatts, measuredAtUtc);

            // Only the leading topic persists a sample, so summing phases does not multiply the sample rate.
            return string.Equals(topic, topics.HouseConsumptionTopic, StringComparison.Ordinal);
        }

        return false;
    }

    private static TelemetrySignalKind? GetTelemetrySignalKind(string topic, VictronMqttTopics topics)
    {
        if (string.Equals(topic, topics.BatterySocTopic, StringComparison.Ordinal))
        {
            return TelemetrySignalKind.StateOfCharge;
        }

        if (string.Equals(topic, topics.GridPowerTopic, StringComparison.Ordinal) ||
            string.Equals(topic, topics.BatteryPowerTopic, StringComparison.Ordinal) ||
            topics.HouseConsumptionTopics.Contains(topic, StringComparer.Ordinal))
        {
            return TelemetrySignalKind.Power;
        }

        return null;
    }

    private async Task PublishKeepAliveAsync(VictronMqttSettings settings, bool suppressRepublish, CancellationToken cancellationToken)
    {
        var client = mqttClient;
        if (client is null || !client.IsConnected)
        {
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"R/{settings.PortalId}/keepalive")
            .WithPayload(CreateKeepAlivePayload(suppressRepublish))
            .Build();

        await client.PublishAsync(message, cancellationToken);
        logger.LogDebug(
            "Victron MQTT KeepAlive gesendet. SuppressRepublish={SuppressRepublish}",
            suppressRepublish);
    }

    private static string CreateClientId(VictronMqttSettings settings)
    {
        var machineName = Environment.MachineName
            .Replace(" ", "-", StringComparison.Ordinal)
            .ToLowerInvariant();

        return $"tibber-victron-controller-{settings.PortalId}-{machineName}-{Environment.ProcessId}";
    }

    private async Task PersistLiveConsumptionSampleAsync(
        MqttTelemetrySnapshotStore snapshotStore,
        decimal houseConsumptionWatts,
        DateTimeOffset measuredAtUtc)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var liveConsumptionRepository = scope.ServiceProvider.GetRequiredService<ILiveConsumptionRepository>();
        var snapshot = snapshotStore.GetSnapshot();
        var supplementaryPvSnapshot = serviceProvider.GetService<HagerEnergyTelemetrySnapshotStore>()?.Current;
        var persistedHouseConsumptionWatts = TryCalculateSiteConsumptionWatts(snapshot, supplementaryPvSnapshot, measuredAtUtc)
            ?? GetPersistedHouseConsumptionWatts(snapshot, houseConsumptionWatts);

        await liveConsumptionRepository.SaveSampleAsync(
            new LiveConsumptionSample(
                persistedHouseConsumptionWatts,
                measuredAtUtc,
                snapshot.GridPowerWatts,
                snapshot.BatteryPowerWatts,
                snapshot.BatterySocPercent,
                GetPersistedPvProductionWatts(snapshot, persistedHouseConsumptionWatts)));

        await CleanupOldLiveSamplesIfDueAsync(scope.ServiceProvider, measuredAtUtc);
    }

    private async Task CleanupOldLiveSamplesIfDueAsync(
        IServiceProvider scopedServiceProvider,
        DateTimeOffset measuredAtUtc)
    {
        if (measuredAtUtc < nextLiveSampleCleanupAtUtc)
        {
            return;
        }

        nextLiveSampleCleanupAtUtc = measuredAtUtc.Add(LiveSampleCleanupInterval);
        var settingsStore = scopedServiceProvider.GetRequiredService<IControllerSettingStore>();
        var retentionDays = await GetLiveSampleRetentionDaysAsync(settingsStore);
        var thresholdUtc = measuredAtUtc.AddDays(-retentionDays);
        var repository = scopedServiceProvider.GetRequiredService<ILiveConsumptionRepository>();
        var deletedCount = await repository.DeleteSamplesOlderThanAsync(thresholdUtc);

        if (deletedCount > 0)
        {
            logger.LogInformation(
                "Alte Live-Messwerte bereinigt. RetentionDays={RetentionDays}, ThresholdUtc={ThresholdUtc}, DeletedCount={DeletedCount}",
                retentionDays,
                thresholdUtc,
                deletedCount);
        }
    }

    private static async Task<int> GetLiveSampleRetentionDaysAsync(IControllerSettingStore settingsStore)
    {
        var setting = await settingsStore.GetSettingAsync(ControllerSettingDefaults.TelemetryLiveSampleRetentionDaysKey);
        if (setting is null ||
            !setting.IsConfigured ||
            !int.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays) ||
            retentionDays < 1)
        {
            return DefaultLiveSampleRetentionDays;
        }

        return retentionDays;
    }

    /// <summary>
    /// Calculates the site consumption that the consumption forecast learns from.
    /// </summary>
    /// <remarks>
    /// Victron's AC consumption does not know the PV and battery behind the E3/DC system and is clipped per phase.
    /// Grid power minus the Victron battery action plus the E3/DC PV production yields the gross site demand on all phases,
    /// so the forecast can subtract the PV forecast exactly once. The E3/DC battery effect intentionally stays in the value,
    /// because that energy is not available to the Victron battery. The DC battery power is used without loss correction;
    /// the resulting error of a few percent is acceptable for an averaged profile.
    /// </remarks>
    public static decimal? TryCalculateSiteConsumptionWatts(
        MqttTelemetrySnapshot snapshot,
        HagerEnergyTelemetrySnapshot? supplementaryPvSnapshot,
        DateTimeOffset measuredAtUtc)
    {
        if (snapshot.GridPowerWatts is not { } gridPowerWatts ||
            snapshot.BatteryPowerWatts is not { } batteryPowerWatts ||
            supplementaryPvSnapshot is null ||
            measuredAtUtc - supplementaryPvSnapshot.LastSuccessfulPollAtUtc > SupplementaryPvMaxAge)
        {
            return null;
        }

        return Math.Max(0m, gridPowerWatts - batteryPowerWatts + supplementaryPvSnapshot.PvProductionWatts);
    }

    private static decimal GetPersistedHouseConsumptionWatts(MqttTelemetrySnapshot snapshot, decimal houseConsumptionWatts)
    {
        if (houseConsumptionWatts != 0m)
        {
            return houseConsumptionWatts;
        }

        return snapshot.GridPowerWatts ?? 0m;
    }

    private static decimal? GetPersistedPvProductionWatts(
        MqttTelemetrySnapshot snapshot,
        decimal persistedHouseConsumptionWatts)
    {
        var sourceWatts = snapshot.HouseConsumptionWatts ?? persistedHouseConsumptionWatts;

        return sourceWatts < 0m ? Math.Abs(sourceWatts) : null;
    }

    private async Task<VictronMqttSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var settingsProvider = scope.ServiceProvider.GetRequiredService<DatabaseVictronMqttSettingsProvider>();

        return await settingsProvider.GetSettingsAsync(cancellationToken);
    }

    private async Task DisconnectClientAsync(CancellationToken cancellationToken)
    {
        await connectionLock.WaitAsync(cancellationToken);
        try
        {
            await DisconnectClientCoreAsync(cancellationToken);
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private async Task DisconnectClientCoreAsync(CancellationToken cancellationToken)
    {
        if (mqttClient is not null)
        {
            if (mqttClient.IsConnected)
            {
                await mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
            }

            mqttClient.Dispose();
        }

        mqttClient = null;
        activeSettings = null;
    }

    private async Task SaveConnectionFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        await SaveOperationalEventAsync(
            "VictronMqtt",
            "Error",
            "Victron MQTT-Verbindung fehlgeschlagen.",
            JsonSerializer.Serialize(new
            {
                Error = exception.Message,
                ExceptionType = exception.GetType().FullName
            }),
            cancellationToken);
    }

    private async Task SaveControlPublishFailureAsync(
        VictronMqttSettings settings,
        string topic,
        int value,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await SaveOperationalEventAsync(
            "VictronMqttControl",
            "Error",
            "Victron MQTT-Schreibbefehl fehlgeschlagen.",
            JsonSerializer.Serialize(new
            {
                settings.Host,
                settings.Port,
                settings.PortalId,
                Topic = topic,
                Value = value,
                Error = exception.Message,
                ExceptionType = exception.GetType().FullName
            }),
            cancellationToken);
    }

    private async Task SaveOperationalEventAsync(
        string category,
        string severity,
        string message,
        string details,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = serviceProvider.CreateAsyncScope();
            var utcClock = scope.ServiceProvider.GetRequiredService<IUtcClock>();
            var operationalEventRepository = scope.ServiceProvider.GetRequiredService<IOperationalEventRepository>();
            await operationalEventRepository.SaveEventAsync(
                new OperationalEvent(Guid.NewGuid(), utcClock.UtcNow, category, severity, message, details),
                cancellationToken);
        }
        catch (Exception persistenceException)
        {
            logger.LogError(persistenceException, "Victron MQTT Operational Event konnte nicht gespeichert werden.");
        }
    }

    private static bool SettingsMatch(VictronMqttSettings left, VictronMqttSettings right)
    {
        return string.Equals(left.Host, right.Host, StringComparison.Ordinal) &&
            left.Port == right.Port &&
            string.Equals(left.PortalId, right.PortalId, StringComparison.Ordinal) &&
            left.KeepAliveSeconds == right.KeepAliveSeconds &&
            string.Equals(left.GridPowerTopicTemplate, right.GridPowerTopicTemplate, StringComparison.Ordinal) &&
            string.Equals(left.BatterySocTopicTemplate, right.BatterySocTopicTemplate, StringComparison.Ordinal) &&
            string.Equals(left.BatteryPowerTopicTemplate, right.BatteryPowerTopicTemplate, StringComparison.Ordinal) &&
            string.Equals(left.HouseConsumptionTopicTemplate, right.HouseConsumptionTopicTemplate, StringComparison.Ordinal) &&
            string.Equals(left.ChargeDischargeSetpointTopic, right.ChargeDischargeSetpointTopic, StringComparison.Ordinal) &&
            string.Equals(left.Hub4ModeTopic, right.Hub4ModeTopic, StringComparison.Ordinal) &&
            left.SwitchEssModeViaMqtt == right.SwitchEssModeViaMqtt &&
            left.ExternalEssPhaseCount == right.ExternalEssPhaseCount &&
            string.Equals(left.ExternalEssL1AcPowerSetpointTopic, right.ExternalEssL1AcPowerSetpointTopic, StringComparison.Ordinal) &&
            string.Equals(left.ExternalEssL2AcPowerSetpointTopic, right.ExternalEssL2AcPowerSetpointTopic, StringComparison.Ordinal) &&
            string.Equals(left.ExternalEssL3AcPowerSetpointTopic, right.ExternalEssL3AcPowerSetpointTopic, StringComparison.Ordinal) &&
            string.Equals(left.DisableChargeTopic, right.DisableChargeTopic, StringComparison.Ordinal) &&
            string.Equals(left.DisableFeedInTopic, right.DisableFeedInTopic, StringComparison.Ordinal) &&
            left.BatteryIdleThresholdWatts == right.BatteryIdleThresholdWatts;
    }
}
