using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class VictronMqttClient
{
    private readonly ILogger<VictronMqttClient> _logger;
    private readonly VictronOptions _options;
    private readonly IMqttClient _client;
    private readonly MqttClientFactory _mqttFactory = new();
    private readonly object _sync = new();

    private EnergyState _state = new();
    private CancellationTokenSource _keepAliveCts;
    private object _keepAliveTask;

    public VictronMqttClient(ILogger<VictronMqttClient> logger, IOptions<VictronOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _client = _mqttFactory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId($"controller-{Guid.NewGuid():N}")
            .Build();

        await _client.ConnectAsync(mqttOptions, cancellationToken);

        var topics = new[]
        {
            ReplacePortal(_options.Topics.GridPower),
            ReplacePortal(_options.Topics.BatterySoc),
            ReplacePortal(_options.Topics.BatteryPower),
            ReplacePortal(_options.Topics.HouseConsumption),
            ReplacePortal(_options.Topics.PvPower)
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct()
        .ToList();

        var subscribeOptions = _mqttFactory.CreateSubscribeOptionsBuilder();
        foreach (var topic in topics)
        {
            subscribeOptions.WithTopicFilter(topic);
        }

        await _client.SubscribeAsync(subscribeOptions.Build(), cancellationToken);

        _keepAliveCts?.Cancel();
        _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _keepAliveTask = Task.Run(async () =>
        {
            while (!_keepAliveCts.IsCancellationRequested)
            {
                try
                {
                    var keepAliveMessage = new MqttApplicationMessageBuilder()
                        .WithTopic($"R/{_options.PortalId}/keepalive")
                        .WithPayload("")
                        .Build();

                    await _client.PublishAsync(keepAliveMessage, _keepAliveCts.Token);
                    await Task.Delay(TimeSpan.FromSeconds(_options.KeepAliveSeconds), _keepAliveCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Victron keepalive failed");
                    await Task.Delay(TimeSpan.FromSeconds(5), _keepAliveCts.Token);
                }
            }
        }, _keepAliveCts.Token);


        _logger.LogInformation("Connected to Victron MQTT {Host}:{Port}", _options.Host, _options.Port);
    }

    public EnergyState GetCurrentState()
    {
        lock (_sync)
        {
            return _state with { };
        }
    }

    public async Task ApplyDecisionAsync(Decision decision, CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            _logger.LogInformation("DryRun: {Action} {Target}W - {Reason}", decision.Action, decision.TargetPowerWatts, decision.Reason);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.WriteTopics.ChargeDischargeSetpoint))
        {
            var topic = $"W/{_options.PortalId}/{_options.WriteTopics.ChargeDischargeSetpoint}";
            var payload = JsonSerializer.Serialize(new
            {
                value = decision.Action switch
                {
                    BatteryAction.Charge => Math.Abs(decision.TargetPowerWatts),
                    BatteryAction.Discharge => -Math.Abs(decision.TargetPowerWatts),
                    _ => 0
                }
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .Build();

            await _client.PublishAsync(message, cancellationToken);
        }
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.Payload.IsEmpty
            ? string.Empty
            : Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Task.CompletedTask;
        }

        var value = TryReadValue(payload);
        if (value is null)
        {
            return Task.CompletedTask;
        }

        lock (_sync)
        {
            if (TopicMatches(topic, _options.Topics.GridPower))
            {
                _state = _state with { GridPowerWatts = value.Value };
            }
            else if (TopicMatches(topic, _options.Topics.BatterySoc))
            {
                _state = _state with { BatterySocPercent = value.Value };
            }
            else if (TopicMatches(topic, _options.Topics.BatteryPower))
            {
                _state = _state with { BatteryPowerWatts = value.Value };
            }
            else if (TopicMatches(topic, _options.Topics.HouseConsumption))
            {
                _state = _state with { HouseConsumptionWatts = value.Value };
            }
            else if (TopicMatches(topic, _options.Topics.PvPower))
            {
                _state = _state with { PvPowerWatts = value.Value };
            }
        }

        return Task.CompletedTask;
    }

    private string ReplacePortal(string topic) => topic.Replace("{portalId}", _options.PortalId, StringComparison.OrdinalIgnoreCase);

    private bool TopicMatches(string actualTopic, string configuredPattern)
    {
        var pattern = ReplacePortal(configuredPattern);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var patternParts = pattern.Split('/');
        var actualParts = actualTopic.Split('/');

        if (patternParts.Length != actualParts.Length)
        {
            return false;
        }

        for (var i = 0; i < patternParts.Length; i++)
        {
            if (patternParts[i] == "+")
            {
                continue;
            }

            if (!string.Equals(patternParts[i], actualParts[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static double? TryReadValue(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("value", out var valueElement))
            {
                return null;
            }

            return valueElement.ValueKind switch
            {
                JsonValueKind.Number => valueElement.GetDouble(),
                JsonValueKind.String when double.TryParse(valueElement.GetString(), out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
