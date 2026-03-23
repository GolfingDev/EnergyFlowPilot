using Microsoft.Extensions.Options;
using TibberVictronController.Web.Models;

namespace TibberVictronController.Web.Services;

public class EnergyControllerService : BackgroundService
{
    private readonly ILogger<EnergyControllerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VictronMqttClient _victron;
    private readonly TibberPriceProvider _tibber;
    private readonly ControllerOptions _options;

    public EnergyControllerService(
        ILogger<EnergyControllerService> logger,
        IServiceScopeFactory scopeFactory,
        VictronMqttClient victron,
        TibberPriceProvider tibber,
        IOptions<ControllerOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _victron = victron;
        _tibber = tibber;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await _victronMqttClient.StartAsync(stoppingToken);

    while (!stoppingToken.IsCancellationRequested)
    {
        try
        {
            var snapshot = _victronMqttClient.GetSnapshot();

            if (snapshot.IsStale)
            {
                _logger.LogWarning(
                    "Skipping control cycle because MQTT data is stale. Last update: {LastUpdateUtc:O}",
                    snapshot.LastMessageUtc);

                await Task.Delay(TimeSpan.FromSeconds(_options.DecisionLoopSeconds), stoppingToken);
                continue;
            }

            var state = snapshot.State;

            // normale Verarbeitung hier
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Controller loop failed");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
}
