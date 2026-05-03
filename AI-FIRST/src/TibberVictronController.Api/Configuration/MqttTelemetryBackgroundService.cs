using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Connects to a generic MQTT device, subscribes to telemetry topics and keeps the latest snapshot in memory.
/// </summary>
public sealed class MqttTelemetryBackgroundService : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<MqttTelemetryBackgroundService> logger;
    private readonly VictronMqttRuntimeStatus runtimeStatus;
    private IMqttClient? mqttClient;

    public MqttTelemetryBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<MqttTelemetryBackgroundService> logger,
        VictronMqttRuntimeStatus runtimeStatus)
    {
        this.serviceProvider = serviceProvider;
        this.logger = logger;
        this.runtimeStatus = runtimeStatus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                runtimeStatus.MarkStarting();
                await RunClientLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                runtimeStatus.MarkFailed($"MQTT-Geräteanbindung konnte nicht initialisiert werden: {exception.Message}");
                logger.LogError(exception, "MQTT-Geräteanbindung konnte nicht initialisiert oder betrieben werden.");
                await Task.Delay(RetryDelay, stoppingToken);
            }
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

    private async Task HandleMessageAsync(
        MqttApplicationMessageReceivedEventArgs eventArguments,
        MqttTelemetryTopics topics,
        MqttTelemetrySnapshotStore snapshotStore)
    {
        var payload = Encoding.UTF8.GetString(eventArguments.ApplicationMessage.Payload.ToArray());

        if (!Dal.Victron.VictronMqttPayloadParser.TryParseDecimal(payload, out var value))
        {
            logger.LogWarning("MQTT-Payload konnte nicht geparst werden. Topic={Topic}", eventArguments.ApplicationMessage.Topic);
            return;
        }

        var measuredAtUtc = DateTimeOffset.UtcNow;
        runtimeStatus.MarkMessageReceived(measuredAtUtc);

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
            await PersistLiveConsumptionSampleAsync(value, measuredAtUtc);
        }
    }

    private async Task RunClientLoopAsync(CancellationToken stoppingToken)
    {
        using var scope = serviceProvider.CreateScope();
        var settingsProvider = scope.ServiceProvider.GetRequiredService<DatabaseMqttDeviceSettingsProvider>();
        var snapshotStore = scope.ServiceProvider.GetRequiredService<MqttTelemetrySnapshotStore>();
        var settings = await settingsProvider.GetSettingsAsync(stoppingToken);
        var topics = CreateTopics(settings);

        mqttClient = new MqttClientFactory().CreateMqttClient();
        mqttClient.ApplicationMessageReceivedAsync += async eventArguments =>
        {
            await HandleMessageAsync(eventArguments, topics, snapshotStore);
        };

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(settings.KeepAliveSeconds))
            .WithClientId($"energy-flow-pilot-{settings.DeviceId}")
            .Build();
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

        await mqttClient.ConnectAsync(options, stoppingToken);
        runtimeStatus.MarkConnected();
        await mqttClient.SubscribeAsync(subscribeOptions, stoppingToken);

        logger.LogInformation("MQTT-Geräteanbindung verbunden. DeviceId={DeviceId}, Topics={TopicCount}", settings.DeviceId, topicFilters.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private static MqttTelemetryTopics CreateTopics(MqttDeviceSettings settings)
    {
        return new MqttTelemetryTopics
        {
            GridPowerTopic = ResolveTopic(settings.GridPowerTopicTemplate, settings.DeviceId),
            BatterySocTopic = ResolveTopic(settings.BatterySocTopicTemplate, settings.DeviceId),
            BatteryPowerTopic = ResolveTopic(settings.BatteryPowerTopicTemplate, settings.DeviceId),
            HouseConsumptionTopic = ResolveTopic(settings.HouseConsumptionTopicTemplate, settings.DeviceId),
            ChargeDischargeSetpointTopic = ResolveTopic(settings.ChargeDischargeSetpointTopic, settings.DeviceId)
        };
    }

    private static string ResolveTopic(string topicTemplate, string deviceId)
    {
        return topicTemplate.Replace("{portalId}", deviceId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task PersistLiveConsumptionSampleAsync(decimal houseConsumptionWatts, DateTimeOffset measuredAtUtc)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var liveConsumptionRepository = scope.ServiceProvider.GetRequiredService<ILiveConsumptionRepository>();

        await liveConsumptionRepository.SaveSampleAsync(
            new LiveConsumptionSample(houseConsumptionWatts, measuredAtUtc));
    }
}
