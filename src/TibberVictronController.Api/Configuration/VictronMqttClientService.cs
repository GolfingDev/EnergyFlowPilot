using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TibberVictronController.Api.Dashboard;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
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

    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<VictronMqttClientService> logger;
    private readonly VictronMqttRuntimeStatus runtimeStatus;
    private readonly SemaphoreSlim connectionLock = new(1, 1);
    private IMqttClient? mqttClient;
    private VictronMqttSettings? activeSettings;

    public VictronMqttClientService(
        IServiceProvider serviceProvider,
        ILogger<VictronMqttClientService> logger,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
        this.runtimeStatus = runtimeStatus;
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

        var nextKeepAliveAtUtc = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = activeSettings ?? throw new InvalidOperationException("Victron MQTT ist nicht konfiguriert.");
            var keepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, settings.KeepAliveSeconds));
            var staleAfter = TimeSpan.FromSeconds(Math.Max(5, settings.StaleAfterSeconds));
            var utcNow = DateTimeOffset.UtcNow;

            if (mqttClient is null || !mqttClient.IsConnected)
            {
                throw new InvalidOperationException("Victron MQTT-Verbindung wurde unterbrochen und wird neu aufgebaut.");
            }

            if (utcNow >= nextKeepAliveAtUtc)
            {
                await PublishKeepAliveAsync(settings, stoppingToken);
                nextKeepAliveAtUtc = utcNow.Add(keepAliveInterval);
            }

            if (!SettingsMatch(settings, await ReadSettingsAsync(stoppingToken)))
            {
                runtimeStatus.MarkFailed("Victron MQTT-Konfiguration wurde geaendert. Die Verbindung wird neu aufgebaut.");
                throw new InvalidOperationException("Victron MQTT-Konfiguration wurde geaendert. Die Verbindung wird neu aufgebaut.");
            }

            if (IsTelemetryStale(utcNow, staleAfter))
            {
                runtimeStatus.MarkFailed("Victron MQTT liefert keine frischen Telemetriedaten mehr. Die Verbindung wird neu aufgebaut.");
                throw new InvalidOperationException("Victron MQTT liefert keine frischen Telemetriedaten mehr. Die Verbindung wird neu aufgebaut.");
            }

            await Task.Delay(LoopDelay, stoppingToken);
        }
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
            snapshotStore.Clear();

            var client = new MqttClientFactory().CreateMqttClient();
            client.ApplicationMessageReceivedAsync += async eventArguments =>
            {
                await HandleMessageAsync(eventArguments, topics, snapshotStore);
            };
            client.DisconnectedAsync += eventArguments =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    runtimeStatus.MarkFailed("Victron MQTT-Verbindung wurde unterbrochen und wird neu aufgebaut.");
                    logger.LogWarning(
                        eventArguments.Exception,
                        "Victron MQTT-Verbindung wurde unterbrochen. Ein neuer Verbindungsaufbau wird vorbereitet.");
                }

                return Task.CompletedTask;
            };

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.Host, settings.Port)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
                .WithClientId($"tibber-victron-controller-{settings.PortalId}")
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
                "Victron MQTT verbunden. PortalId={PortalId}, Host={Host}, Port={Port}, Topics={TopicCount}",
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
        runtimeStatus.MarkMessageReceived(measuredAtUtc);

        var shouldPersistConsumptionSample = ApplyTelemetryValue(
            eventArguments.ApplicationMessage.Topic,
            value,
            measuredAtUtc,
            topics,
            snapshotStore);

        if (shouldPersistConsumptionSample)
        {
            await PersistLiveConsumptionSampleAsync(GetPersistedHouseConsumptionWatts(snapshotStore, value), measuredAtUtc);
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
        MqttTelemetrySnapshotStore snapshotStore)
    {
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

        if (string.Equals(topic, topics.HouseConsumptionTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateHouseConsumption(value, measuredAtUtc);
            return true;
        }

        return false;
    }

    private async Task PublishKeepAliveAsync(VictronMqttSettings settings, CancellationToken cancellationToken)
    {
        var client = mqttClient;
        if (client is null || !client.IsConnected)
        {
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic($"R/{settings.PortalId}/keepalive")
            .WithPayload(string.Empty)
            .Build();

        await client.PublishAsync(message, cancellationToken);
    }

    private async Task PersistLiveConsumptionSampleAsync(decimal houseConsumptionWatts, DateTimeOffset measuredAtUtc)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var liveConsumptionRepository = scope.ServiceProvider.GetRequiredService<ILiveConsumptionRepository>();

        await liveConsumptionRepository.SaveSampleAsync(
            new LiveConsumptionSample(houseConsumptionWatts, measuredAtUtc));
    }

    private static decimal GetPersistedHouseConsumptionWatts(MqttTelemetrySnapshotStore snapshotStore, decimal houseConsumptionWatts)
    {
        if (houseConsumptionWatts != 0m)
        {
            return houseConsumptionWatts;
        }

        return snapshotStore.GetSnapshot().GridPowerWatts ?? 0m;
    }

    private bool IsTelemetryStale(DateTimeOffset utcNow, TimeSpan staleAfter)
    {
        var lastSuccessfulMessageAtUtc = runtimeStatus.LastSuccessfulMessageAtUtc;

        if (lastSuccessfulMessageAtUtc is null)
        {
            var connectedAtUtc = runtimeStatus.ConnectedAtUtc;

            return connectedAtUtc is not null && utcNow - connectedAtUtc.Value > staleAfter;
        }

        return utcNow - lastSuccessfulMessageAtUtc.Value > staleAfter;
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
            left.StaleAfterSeconds == right.StaleAfterSeconds &&
            left.DryRun == right.DryRun &&
            left.ControlMode == right.ControlMode &&
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
