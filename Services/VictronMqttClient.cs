using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class VictronMqttClient : IAsyncDisposable
{
    private readonly ILogger<VictronMqttClient> _logger;
    private readonly VictronOptions _options;
    private readonly object _sync = new();

    private IMqttClient? _client;
    private Task? _backgroundTask;
    private Task? _keepAliveTask;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _keepAliveCts;
    private bool _started;

    private EnergyState _state = new();
    private DateTime _lastMessageUtc = DateTime.MinValue;

    public VictronMqttClient(ILogger<VictronMqttClient> logger, IOptions<VictronOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = Task.Run(() => RunLoopAsync(_runCts.Token), _runCts.Token);
        return Task.CompletedTask;
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

    public async Task ApplyDecisionAsync(Decision decision, CancellationToken cancellationToken)
    {
        if (_options.DryRun)
        {
            _logger.LogInformation("DryRun: {Action} {Target}W - {Reason}", decision.Action, decision.TargetPowerWatts, decision.Reason);
            return;
        }

        var client = _client;
        if (client is null || !client.IsConnected || string.IsNullOrWhiteSpace(_options.WriteTopics.ChargeDischargeSetpoint))
        {
            _logger.LogWarning("Victron MQTT write skipped because client is not connected.");
            return;
        }

        var signedPower = decision.Action switch
        {
            BatteryAction.Charge => Math.Abs(decision.TargetPowerWatts),
            BatteryAction.Discharge => -Math.Abs(decision.TargetPowerWatts),
            _ => 0
        };

        var topic = $"W/{_options.PortalId}/{_options.WriteTopics.ChargeDischargeSetpoint}";
        var payload = JsonSerializer.Serialize(new { value = signedPower });

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();

        await client.PublishAsync(message, cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var snapshot = GetSnapshot();
                    if (_client is null || !_client.IsConnected)
                    {
                        _logger.LogWarning("Victron MQTT disconnected. Reconnecting...");
                        break;
                    }

                    if (snapshot.IsStale)
                    {
                        _logger.LogWarning("No fresh Victron MQTT data since {LastUpdateUtc:O}. Restarting MQTT connection...", snapshot.LastMessageUtc);
                        await RestartConnectionAsync(cancellationToken);
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Victron MQTT loop failed. Retrying...");
                await CleanupClientAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        await CleanupClientAsync();

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                HandleMessage(e);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Victron message processing failed");
            }

            return Task.CompletedTask;
        };

        _client.DisconnectedAsync += e =>
        {
            _logger.LogWarning("Victron MQTT disconnected: {Reason}", e.ReasonString);
            return Task.CompletedTask;
        };

        var mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId($"controller-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();

        await _client.ConnectAsync(mqttOptions, cancellationToken);

        var subscribeBuilder = factory.CreateSubscribeOptionsBuilder();
        foreach (var topic in BuildTopicList())
        {
            subscribeBuilder.WithTopicFilter(topic);
        }

        await _client.SubscribeAsync(subscribeBuilder.Build(), cancellationToken);
        StartKeepAliveLoop(cancellationToken);
        _logger.LogInformation("Connected to Victron MQTT {Host}:{Port}", _options.Host, _options.Port);
    }

    private List<string> BuildTopicList()
    {
        return new[]
        {
            _options.Topics.GridPower,
            _options.Topics.BatterySoc,
            _options.Topics.BatteryPower,
            _options.Topics.HouseConsumption,
            _options.Topics.PvPower
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(ResolveTopic)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
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
                    var client = _client;
                    if (client is { IsConnected: true })
                    {
                        var keepAlive = new MqttApplicationMessageBuilder()
                            .WithTopic($"R/{_options.PortalId}/keepalive")
                            .WithPayload(string.Empty)
                            .Build();

                        await client.PublishAsync(keepAlive, token);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.KeepAliveSeconds)), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Victron keepalive failed");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }, token);
    }

    private async Task RestartConnectionAsync(CancellationToken cancellationToken)
    {
        await CleanupClientAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await EnsureConnectedAsync(cancellationToken);
    }

    private void HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = !e.ApplicationMessage.Payload.IsEmpty
            ? Encoding.UTF8.GetString(e.ApplicationMessage.Payload)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        var value = TryReadValue(payload);
        if (value is null)
        {
            return;
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
    }

    private string ResolveTopic(string configuredTopic)
        => configuredTopic.Replace("{portalId}", _options.PortalId, StringComparison.OrdinalIgnoreCase);

    private bool TopicMatches(string actualTopic, string configuredTopic)
    {
        var pattern = ResolveTopic(configuredTopic);
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

    private static double? TryReadValue(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("value", out var valueElement))
            {
                return null;
            }

            return valueElement.ValueKind switch
            {
                JsonValueKind.Number => valueElement.GetDouble(),
                JsonValueKind.String when double.TryParse(valueElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
    }

    private async Task CleanupClientAsync()
    {
        try
        {
            _keepAliveCts?.Cancel();
        }
        catch
        {
        }

        if (_keepAliveTask is not null)
        {
            try
            {
                await _keepAliveTask;
            }
            catch
            {
            }
        }

        if (_client is not null)
        {
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync();
                }
            }
            catch
            {
            }

             _client.Dispose();
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
        }

        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask;
            }
            catch
            {
            }
        }

        await CleanupClientAsync();
        _runCts?.Dispose();
        _keepAliveCts?.Dispose();
    }
}
