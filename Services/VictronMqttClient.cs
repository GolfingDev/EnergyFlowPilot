using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Internal;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class VictronMqttClient : IAsyncDisposable
{
    private readonly ILogger<VictronMqttClient> _logger;
    private readonly VictronOptions _options;
    private readonly IMqttClient _client;
    private readonly MqttClientFactory _mqttFactory = new();
    private readonly object _sync = new();

    private EnergyState _state = new();
    private DateTime _lastMessageUtc = DateTime.MinValue;
    private Task? _keepAliveTask;
    private CancellationTokenSource? _keepAliveCts;

    public VictronMqttClient(ILogger<VictronMqttClient> logger, IOptions<VictronOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _client = _mqttFactory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task ReConnectAsync(CancellationToken cancellationToken)
    {
        await _client.DisconnectAsync();
        _keepAliveTask.Dispose();
        _keepAliveCts.TryCancel();

        await ConnectAsync(cancellationToken);
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
        await PublishKeepAliveAsync(cancellationToken);
        StartKeepAliveLoop(cancellationToken);

        _logger.LogInformation("Connected to Victron MQTT {Host}:{Port}", _options.Host, _options.Port);
    }

    public EnergyStateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var age = _lastMessageUtc == DateTime.MinValue
                ? TimeSpan.MaxValue
                : DateTime.UtcNow - _lastMessageUtc;

            return new EnergyStateSnapshot(
                _state with { },
                _lastMessageUtc,
                age > TimeSpan.FromSeconds(Math.Max(5, _options.StaleAfterSeconds)));
        }
    }

    public EnergyState GetCurrentState() => GetSnapshot().State;

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

            _lastMessageUtc = DateTime.UtcNow;
        }

        return Task.CompletedTask;
    }

    private void StartKeepAliveLoop(CancellationToken cancellationToken)
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _keepAliveCts.Token;

        _keepAliveTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.KeepAliveSeconds)), token);
                    await PublishKeepAliveAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Victron keepalive failed");
                }
            }
        }, token);
    }

    private async Task PublishKeepAliveAsync(CancellationToken cancellationToken)
    {
        var keepAlive = new MqttApplicationMessageBuilder()
            .WithTopic($"R/{_options.PortalId}/keepalive")
            .WithPayload("")
            .Build();

        await _client.PublishAsync(keepAlive, cancellationToken);
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

    public async ValueTask DisposeAsync()
    {
        _keepAliveCts?.Cancel();
        if (_keepAliveTask is not null)
        {
            try { await _keepAliveTask; } catch { }
        }
        _keepAliveCts?.Dispose();
        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }
        _client.Dispose();
    }
}
