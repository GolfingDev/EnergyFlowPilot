using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Dashboard;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Homematic;
using TibberVictronController.Business.Services;
using TibberVictronController.Api.Dashboard;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Consumption;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Homematic;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;
using TibberVictronController.Dal.Tibber;
using TibberVictronController.Dal.Victron;
using TibberVictronController.Dal.Weather;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Registers controller services and keeps Program.cs focused on the application pipeline.
/// </summary>
public static class ControllerServiceCollectionExtensions
{
    private const string ControllerDatabaseConnectionName = "ControllerDatabase";

    /// <summary>
    /// Registers SQLite persistence, repositories and application services used by the API.
    /// </summary>
    public static IServiceCollection AddControllerApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ControllerDatabaseConnectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Die SQLite-Connection-String-Einstellung 'ControllerDatabase' fehlt.");
        }

        services.AddSingleton<IUtcClock, SystemUtcClock>();
        services.AddSignalR();
        services.AddDbContext<ControllerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ControllerDbInitializer>();
        services.AddScoped<IControllerSettingStore, EfControllerSettingStore>();
        services.AddScoped<DatabaseMqttDeviceSettingsProvider>();
        services.AddScoped<DatabaseVictronMqttSettingsProvider>();
        services.AddScoped<DatabaseHagerEnergySettingsProvider>();
        services.AddScoped<DatabaseHomematicSettingsProvider>();
        services.AddScoped<DatabaseWeishauptBiblockModbusSettingsProvider>();
        services.AddScoped<DatabaseWeishauptHotWaterSettingsProvider>();
        services.AddScoped<IWeishauptHotWaterControl, WeishauptHotWaterModbusControl>();
        services.AddScoped<IWeishauptHotWaterStateStore, EfWeishauptHotWaterStateStore>();
        services.AddScoped<WeishauptHotWaterControlService>();
        services.AddScoped<WeishauptHotWaterReportService>();
        services.AddScoped<TelemetrySourceSelector>();
        services.AddSingleton<VictronMqttRuntimeStatus>();
        services.AddSingleton<DecisionWorkerRuntimeStatus>();
        services.AddSingleton<DecisionCalculationTrigger>();
        services.AddSingleton<SignificantTelemetryChangeDetector>();
        services.AddSingleton<VictronSetpointRefreshState>();
        services.AddScoped<IDecisionLogRepository, EfDecisionLogRepository>();
        services.AddScoped<IBatterySavingsRepository, EfBatterySavingsRepository>();
        services.AddScoped<IBatterySavingsAccountingService, BatterySavingsAccountingService>();
        services.AddScoped<IOperationalEventRepository, EfOperationalEventRepository>();
        services.AddScoped<ILiveConsumptionRepository, EfLiveConsumptionRepository>();
        services.AddScoped<IHomematicClimateRepository, EfHomematicClimateRepository>();
        services.AddScoped<IWeishauptBiblockTelemetryRepository, EfWeishauptBiblockTelemetryRepository>();
        services.AddScoped<IHomematicClimateHistoryService, HomematicClimateHistoryService>();
        services.AddScoped<IWorkerFailureNotifier, WorkerFailureEmailNotifier>();
        services.AddSingleton<IDashboardLiveUpdatePublisher, SignalRDashboardLiveUpdatePublisher>();
        services.AddScoped<IVictronSetpointPublisher, MqttVictronSetpointPublisher>();
        services.AddSingleton<VictronMqttClientService>();
        services.AddSingleton<IVictronMqttControlClient>(serviceProvider => serviceProvider.GetRequiredService<VictronMqttClientService>());
        services.AddSingleton<MqttTelemetrySnapshotStore>();
        services.AddScoped<IControllerSettingsService, ControllerSettingsService>();
        services.AddScoped<IBatteryConfigurationProvider, DatabaseBatteryConfigurationProvider>();
        services.AddScoped<ConfiguredBatteryStateProvider>();
        services.AddScoped<ConfiguredCurrentSiteTelemetryProvider>();
        services.AddScoped<MqttBatteryStateProvider>();
        services.AddScoped<MqttCurrentSiteTelemetryProvider>();
        services.AddScoped<ResilientBatteryStateProvider>();
        services.AddScoped<ResilientCurrentSiteTelemetryProvider>();
        services.AddSingleton<HagerEnergyTokenCache>();
        services.AddSingleton<HagerEnergyTelemetrySnapshotStore>();
        services.AddHttpClient<HagerEnergyApiClient>();
        services.AddHttpClient<IHomematicClimateSource, HomematicXmlApiClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IWeishauptBiblockTelemetrySource, WeishauptBiblockModbusClient>();
        services.AddScoped<HagerEnergyBatteryStateProvider>();
        services.AddScoped<HagerEnergyCurrentSiteTelemetryProvider>();
        services.AddScoped<IBatteryStateProvider, SelectedBatteryStateProvider>();
        services.AddScoped<ICurrentSiteTelemetryProvider, SelectedCurrentSiteTelemetryProvider>();
        services.AddSingleton<OpenMeteoTemperatureCache>();
        services.AddHttpClient<ITemperatureProvider, OpenMeteoTemperatureProvider>();
        services.AddSingleton<ConsumptionForecastModelCache>();
        services.AddScoped<IHistoricalConsumptionProvider, AverageDailyConsumptionForecastProvider>();
        services.AddSingleton<PriceDriverCache>();
        services.AddHttpClient<IPriceDriverProvider, PriceDriverProvider>();
        services.AddScoped<IBatteryForecastService, BatteryForecastService>();
        services.AddScoped(serviceProvider => new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = serviceProvider.GetRequiredService<IUtcClock>(),
            BatteryStateProvider = serviceProvider.GetRequiredService<IBatteryStateProvider>(),
            BatteryConfigurationProvider = serviceProvider.GetRequiredService<IBatteryConfigurationProvider>(),
            CurrentSiteTelemetryProvider = serviceProvider.GetRequiredService<ICurrentSiteTelemetryProvider>(),
            TibberPriceForecastProvider = serviceProvider.GetRequiredService<ITibberPriceForecastProvider>(),
            BatteryForecastService = serviceProvider.GetRequiredService<IBatteryForecastService>(),
            ControllerSettingStore = serviceProvider.GetRequiredService<IControllerSettingStore>(),
            DecisionLogRepository = serviceProvider.GetRequiredService<IDecisionLogRepository>()
        });
        services.AddScoped<ICurrentBatteryDecisionService, CurrentBatteryDecisionService>();
        services.AddScoped(serviceProvider => new DashboardSummaryServiceDependencies
        {
            UtcClock = serviceProvider.GetRequiredService<IUtcClock>(),
            DecisionLogRepository = serviceProvider.GetRequiredService<IDecisionLogRepository>(),
            LiveConsumptionRepository = serviceProvider.GetRequiredService<ILiveConsumptionRepository>(),
            BatteryForecastService = serviceProvider.GetRequiredService<IBatteryForecastService>(),
            BatteryConfigurationProvider = serviceProvider.GetRequiredService<IBatteryConfigurationProvider>(),
            TibberPriceForecastProvider = serviceProvider.GetRequiredService<ITibberPriceForecastProvider>(),
            ControllerSettingStore = serviceProvider.GetRequiredService<IControllerSettingStore>()
        });
        services.AddScoped<IDashboardSummaryService, DashboardSummaryService>();
        services.AddSingleton<TibberPriceForecastCache>();
        services.AddHttpClient(nameof(TibberPriceForecastProvider));
        services.AddScoped<ITibberPriceForecastProvider>(serviceProvider =>
        {
            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

            return new TibberPriceForecastProvider(
                httpClientFactory.CreateClient(nameof(TibberPriceForecastProvider)),
                serviceProvider.GetRequiredService<IControllerSettingStore>(),
                serviceProvider.GetRequiredService<TibberPriceForecastCache>());
        });
        services.AddSingleton<ForecastSolarPvForecastCache>();
        services.AddHttpClient<ForecastSolarPvForecastProvider>();
        services.AddScoped<IWeatherForecastProvider, CachedWeatherForecastProvider>();
        services.AddHostedService<DecisionExecutionBackgroundService>();
        services.AddHostedService<BatterySavingsAccountingBackgroundService>();
        services.AddHostedService<HagerEnergyPollingBackgroundService>();
        services.AddHostedService<HomematicPollingBackgroundService>();
        services.AddHostedService<WeishauptBiblockModbusPollingBackgroundService>();
        services.AddHostedService<WeishauptHotWaterBackgroundService>();
        services.AddHostedService<VictronSetpointRefreshBackgroundService>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<VictronMqttClientService>());

        return services;
    }
}
