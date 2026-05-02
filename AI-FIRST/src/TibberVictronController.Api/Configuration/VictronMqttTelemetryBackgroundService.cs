using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Connects to Victron MQTT, subscribes to telemetry topics and keeps the latest snapshot in memory.
/// </summary>
public sealed class VictronMqttTelemetryBackgroundService : BackgroundService
{
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<VictronMqttTelemetryBackgroundService> logger;
    private IMqttClient? mqttClient;

    public VictronMqttTelemetryBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<VictronMqttTelemetryBackgroundService> logger)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceProvider.CreateScope();
        var settingsProvider = scope.ServiceProvider.GetRequiredService<DatabaseVictronMqttSettingsProvider>();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<VictronTelemetrySnapshotStore>();
        var settings = await settingsProvider.GetSettingsAsync(stoppingToken);
        var topics = VictronMqttTopicFactory.Create(settings);

        mqttClient = new MqttClientFactory().CreateMqttClient();
        mqttClient.ApplicationMessageReceivedAsync += eventArguments =>
        {
            HandleMessage(eventArguments, topics, snapshotStore);
            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
            .WithClientId($"tibber-victron-controller-{settings.PortalId}")
            .Build();

        await mqttClient.ConnectAsync(options, stoppingToken);

        var topicFilters = topics.ReadTopics
            .Select(topic => new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build())
            .ToList();
        var subscribeOptions = new MqttClientSubscribeOptions
        {
            TopicFilters = topicFilters
        };

        await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);

        logger.LogInformation("Victron MQTT verbunden. PortalId={PortalId}, Topics={TopicCount}", settings.PortalId, topicFilters.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (mqttClient is not null && mqttClient.IsConnected)
        {
            await mqttClient.DisconnectAsync(new MqttClientDisconnectOptions(), cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }

    private void HandleMessage(
        MqttApplicationMessageReceivedEventArgs eventArguments,
        VictronMqttTopics topics,
        VictronTelemetrySnapshotStore snapshotStore)
    {
        var payload = Encoding.UTF8.GetString(eventArguments.ApplicationMessage.Payload.ToArray());

        if (!VictronMqttPayloadParser.TryParseDecimal(payload, out var value))
        {
            logger.LogWarning($"Victron MQTT Payload konnte nicht geparst werden. Topic={Topic}", eventArguments.ApplicationMessage.Topic);
            return;
        }

        var measuredAtUtc = DateTimeOffset.UtcNow;

        if (string.Equals(eventArguments.ApplicationMessage.Topic, topics.GridPowerTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateGridPower(value, measuredAtUtc);
            return;
        }

        if (string.Equals(eventArguments.ApplicationMessage.Topic, topics.BatterySocTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateBatterySoc(value, measuredAtUtc);
            return;
        }

        if (string.Equals(eventArguments.ApplicationMessage.Topic, topics.BatteryPowerTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateBatteryPower(value, measuredAtUtc);
            return;
        }

        if (string.Equals(eventArguments.ApplicationMessage.Topic, topics.HouseConsumptionTopic, StringComparison.Ordinal))
        {
            snapshotStore.UpdateHouseConsumption(value, measuredAtUtc);
        }
    }
}
