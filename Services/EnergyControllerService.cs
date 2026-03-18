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
        await _victron.ConnectAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var decisionEngine = scope.ServiceProvider.GetRequiredService<DecisionEngine>();
                var decisionHistoryStore = scope.ServiceProvider.GetRequiredService<IDecisionHistoryStore>();
                var stateHistoryStore = scope.ServiceProvider.GetRequiredService<IEnergyStateHistoryStore>();

                var prices = await _tibber.GetUpcomingPricesAsync(stoppingToken);
                var state = _victron.GetCurrentState();

                await stateHistoryStore.AddAsync(state, stoppingToken);

                if (prices.Count > 0)
                {
                    var decision = await decisionEngine.BuildDecisionAsync(state, prices, DateTimeOffset.Now, stoppingToken);
                    await decisionHistoryStore.AddAsync(decision, stoppingToken);
                    await _victron.ApplyDecisionAsync(decision, stoppingToken);

                    _logger.LogInformation(
                        "Decision: {Action}, Reason: {Reason}, Grid={GridPower}W, SoC={BatterySoc}%",
                        decision.Action,
                        decision.Reason,
                        state.GridPowerWatts,
                        state.BatterySocPercent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Controller loop failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.DecisionLoopSeconds), stoppingToken);
        }
    }
}
