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
        await _victron.StartAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var decisionEngine = scope.ServiceProvider.GetRequiredService<DecisionEngine>();
                var decisionHistoryStore = scope.ServiceProvider.GetRequiredService<IDecisionHistoryStore>();
                var stateHistoryStore = scope.ServiceProvider.GetRequiredService<IEnergyStateHistoryStore>();

                var snapshot = _victron.GetSnapshot();
                if (snapshot.IsStale)
                {
                    _logger.LogWarning("Skipping controller cycle because live data is stale. Last update: {LastUpdateUtc:O}", snapshot.LastMessageUtc);
                    await Task.Delay(TimeSpan.FromSeconds(_options.DecisionLoopSeconds), stoppingToken);
                    continue;
                }

                var prices = await _tibber.GetUpcomingPricesAsync(stoppingToken);
                if (prices.Count == 0)
                {
                    _logger.LogWarning("Skipping controller cycle because no Tibber prices are available.");
                    await Task.Delay(TimeSpan.FromSeconds(_options.DecisionLoopSeconds), stoppingToken);
                    continue;
                }

                await stateHistoryStore.AddAsync(snapshot.State, stoppingToken);
                var decision = await decisionEngine.BuildDecisionAsync(snapshot.State, prices, DateTimeOffset.Now, stoppingToken);
                await decisionHistoryStore.AddAsync(decision, stoppingToken);
                await _victron.ApplyDecisionAsync(decision, stoppingToken);

                _logger.LogInformation(
                    "Decision: {Action}, Target={TargetPower}W, SoC={Soc}%, Grid={Grid}W, Reason={Reason}",
                    decision.Action,
                    decision.TargetPowerWatts,
                    snapshot.State.BatterySocPercent,
                    snapshot.State.GridPowerWatts,
                    decision.Reason);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller loop failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.DecisionLoopSeconds), stoppingToken);
        }
    }
}
