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
    private readonly MqttClientFactory _mqttFactory = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private IMqttClient? _client;
    private EnergyState _state = new();
    private DateTime _lastMessageUtc = DateTime.MinValue;
    private Task? _keepAliveTask;
    private CancellationTokenSource? _keepAliveCts;
    private Task? _watchdogTask;
    private CancellationTokenSource? _watchdogCts;
    private bool _started;

    public VictronMqttClient(ILogger<VictronMqttClient> logger, IOptions<VictronOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await EnsureConnectedAsync(_watchdogCts.Token);
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token), _watchdogCts.Token);
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

        var client = _client;
        if (client is null || !client.IsConnected)
        {
            _logger.LogWarning("Skipping write command because MQTT client is not connected.");
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

            await client.PublishAsync(message, cancellationToken);
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

                var client = _client;
                if (client is null || !client.IsConnected)
                {
                    _logger.LogWarning("Victron MQTT disconnected. Reconnecting...");
                    await RestartConnectionAsync(cancellationToken);
                    continue;
                }

                var snapshot = GetSnapshot();
                if (snapshot.IsStale)
                {
                    _logger.LogWarning(
                        "No new MQTT data since {LastUpdateUtc:O}. Restarting connection...",
                        snapshot.LastMessageUtc);
                    await RestartConnectionAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Victron MQTT watchdog failed");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task RestartConnectionAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await SafeDisconnectAsync();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            await EnsureConnectedInternalAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedInternalAsync(cancellationToken);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task EnsureConnectedInternalAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        var client = _mqttFactory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        client.DisconnectedAsync += e =>
        {
            _logger.LogWarning("Victron MQTT disconnected: {Reason}", e.ReasonString);
            return Task.CompletedTask;
        };

        var mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId($"controller-{Guid.NewGuid():N}")
            .Build();

        await client.ConnectAsync(mqttOptions, cancellationToken);

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

        await client.SubscribeAsync(subscribeOptions.Build(), cancellationToken);

        _client = client;
        await PublishKeepAliveAsync(client, cancellationToken);
        StartKeepAliveLoop(cancellationToken);

        _logger.LogInformation("Connected to Victron MQTT {Host}:{Port}", _options.Host, _options.Port);
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

                    var client = _client;
                    if (client is { IsConnected: true })
                    {
                        await PublishKeepAliveAsync(client, token);
                    }
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

    private async Task PublishKeepAliveAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        var keepAlive = new MqttApplicationMessageBuilder()
            .WithTopic($"R/{_options.PortalId}/keepalive")
            .WithPayload("")
            .Build();

        await client.PublishAsync(keepAlive, cancellationToken);
    }

    private async Task SafeDisconnectAsync()
    {
        _keepAliveCts?.Cancel();
        if (_keepAliveTask is not null)
        {
            try { await _keepAliveTask; } catch { }
        }
        _keepAliveTask = null;
        _keepAliveCts?.Dispose();
        _keepAliveCts = null;

        if (_client is not null)
        {
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync();
                }
            }
            catch { }

            try
            {
                _client.Dispose();
            }
            catch { }

            _client = null;
        }
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
                JsonValueKind.String when double.TryParse(valueElement.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
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
        _watchdogCts?.Cancel();
        if (_watchdogTask is not null)
        {
            try { await _watchdogTask; } catch { }
        }

        await SafeDisconnectAsync();
        _watchdogCts?.Dispose();
        _connectionLock.Dispose();
    }
}
