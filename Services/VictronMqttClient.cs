using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public interface IVictronMqttClient
{
    Task StartAsync(CancellationToken cancellationToken);
    EnergyStateSnapshot GetSnapshot();
}

public class VictronMqttClient : IVictronMqttClient, IAsyncDisposable
{
    private readonly ILogger<VictronMqttClient> _logger;
    private readonly VictronMqttOptions _options;

    private readonly object _sync = new();

    private IMqttClient? _client;
    private Task? _backgroundTask;
    private Task? _keepAliveTask;
    private CancellationTokenSource? _internalCts;
    private CancellationTokenSource? _keepAliveCts;

    private EnergyState _state = new();
    private DateTime _lastMessageUtc = DateTime.MinValue;
    private bool _started;

    public VictronMqttClient(
        IOptions<VictronMqttOptions> options,
        ILogger<VictronMqttClient> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _backgroundTask = Task.Run(() => RunAsync(_internalCts.Token), _internalCts.Token);

        return Task.CompletedTask;
    }

    public EnergyStateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var age = DateTime.UtcNow - _lastMessageUtc;
            var isStale = _lastMessageUtc == DateTime.MinValue ||
                          age > TimeSpan.FromSeconds(_options.StaleAfterSeconds);

            return new EnergyStateSnapshot
            {
                State = new EnergyState
                {
                    GridPowerWatts = _state.GridPowerWatts,
                    BatterySocPercent = _state.BatterySocPercent,
                    BatteryPowerWatts = _state.BatteryPowerWatts,
                    HouseConsumptionWatts = _state.HouseConsumptionWatts,
                    PvPowerWatts = _state.PvPowerWatts
                },
                LastMessageUtc = _lastMessageUtc,
                IsStale = isStale
            };
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAndSubscribedAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var snapshot = GetSnapshot();

                    if (_client is null || !_client.IsConnected)
                    {
                        _logger.LogWarning("MQTT client disconnected. Reconnecting...");
                        break;
                    }

                    if (snapshot.IsStale)
                    {
                        _logger.LogWarning(
                            "No fresh MQTT data since {LastMessageUtc:O}. Restarting MQTT connection...",
                            snapshot.LastMessageUtc);

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
                await SafeCleanupClientAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task EnsureConnectedAndSubscribedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null && _client.IsConnected)
        {
            return;
        }

        await SafeCleanupClientAsync();

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
                _logger.LogWarning(ex, "Failed to process MQTT message");
            }

            return Task.CompletedTask;
        };

        _client.DisconnectedAsync += e =>
        {
            _logger.LogWarning("Victron MQTT disconnected: {Reason}", e.ReasonString);
            return Task.CompletedTask;
        };

        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithCredentials(_options.Username, _options.Password)
            .WithCleanSession()
            .Build();

        _logger.LogInformation("Connecting to Victron MQTT {Host}:{Port}", _options.Host, _options.Port);
        await _client.ConnectAsync(clientOptions, cancellationToken);

        var topics = BuildTopicList();
        foreach (var topic in topics)
        {
            await _client.SubscribeAsync(topic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken);
        }

        _logger.LogInformation("Subscribed to {Count} Victron MQTT topics", topics.Count);

        StartKeepAliveLoop(cancellationToken);
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
        .Distinct()
        .Select(ResolveTopic)
        .ToList();
    }

    private string ResolveTopic(string topic)
        => topic.Replace("{portalId}", _options.PortalId, StringComparison.OrdinalIgnoreCase);

    private void StartKeepAliveLoop(CancellationToken cancellationToken)
    {
        _keepAliveCts?.Cancel();
        _keepAliveCts?.Dispose();
        _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _keepAliveTask = Task.Run(async () =>
        {
            while (!_keepAliveCts.IsCancellationRequested)
            {
                try
                {
                    if (_client is { IsConnected: true })
                    {
                        var keepAliveMessage = new MqttApplicationMessageBuilder()
                            .WithTopic($"R/{_options.PortalId}/keepalive")
                            .WithPayload("")
                            .Build();

                        await _client.PublishAsync(keepAliveMessage, _keepAliveCts.Token);
                    }

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
    }

    private async Task RestartConnectionAsync(CancellationToken cancellationToken)
    {
        await SafeCleanupClientAsync();

        lock (_sync)
        {
            // bewusst NICHT _state löschen
            // letzter Zustand bleibt sichtbar, aber Snapshot wird stale
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await EnsureConnectedAndSubscribedAsync(cancellationToken);
    }

    private void HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.PayloadSegment.Count > 0
            ? System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        double? value = TryParseVictronPayload(payload);
        if (value is null)
        {
            return;
        }

        lock (_sync)
        {
            if (topic.Equals(ResolveTopic(_options.Topics.GridPower), StringComparison.OrdinalIgnoreCase))
            {
                _state.GridPowerWatts = value.Value;
            }
            else if (topic.Equals(ResolveTopic(_options.Topics.BatterySoc), StringComparison.OrdinalIgnoreCase))
            {
                _state.BatterySocPercent = value.Value;
            }
            else if (topic.Equals(ResolveTopic(_options.Topics.BatteryPower), StringComparison.OrdinalIgnoreCase))
            {
                _state.BatteryPowerWatts = value.Value;
            }
            else if (topic.Equals(ResolveTopic(_options.Topics.HouseConsumption), StringComparison.OrdinalIgnoreCase))
            {
                _state.HouseConsumptionWatts = value.Value;
            }
            else if (topic.Equals(ResolveTopic(_options.Topics.PvPower), StringComparison.OrdinalIgnoreCase))
            {
                _state.PvPowerWatts = value.Value;
            }

            _lastMessageUtc = DateTime.UtcNow;
        }
    }

    private static double? TryParseVictronPayload(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);

            if (doc.RootElement.TryGetProperty("value", out var valueElement))
            {
                return valueElement.ValueKind switch
                {
                    JsonValueKind.Number => valueElement.GetDouble(),
                    JsonValueKind.String when double.TryParse(
                        valueElement.GetString(),
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out var parsed) => parsed,
                    _ => null
                };
            }
        }
        catch
        {
            if (double.TryParse(payload, NumberStyles.Any, CultureInfo.InvariantCulture, out var plain))
            {
                return plain;
            }
        }

        return null;
    }

    private async Task SafeCleanupClientAsync()
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

            try
            {
                await _client.DisposeAsync();
            }
            catch
            {
            }

            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _internalCts?.Cancel();
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

        await SafeCleanupClientAsync();

        _internalCts?.Dispose();
        _keepAliveCts?.Dispose();
    }
}