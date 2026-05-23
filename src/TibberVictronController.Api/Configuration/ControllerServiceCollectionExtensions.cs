using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Services;
using TibberVictronController.Dal.Battery;
using TibberVictronController.Dal.Consumption;
using TibberVictronController.Dal.HagerEnergy;
using TibberVictronController.Dal.Mqtt;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;
using TibberVictronController.Dal.Tibber;
using TibberVictronController.Dal.Victron;
using TibberVictronController.Dal.Weather;

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
        services.AddDbContext<ControllerDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ControllerDbInitializer>();
        services.AddScoped<IControllerSettingStore, EfControllerSettingStore>();
        services.AddScoped<DatabaseMqttDeviceSettingsProvider>();
        services.AddScoped<DatabaseHagerEnergySettingsProvider>();
        services.AddScoped<TelemetrySourceSelector>();
        services.AddSingleton<VictronMqttRuntimeStatus>();
        services.AddSingleton<DecisionWorkerRuntimeStatus>();
        services.AddScoped<IDecisionLogRepository, EfDecisionLogRepository>();
        services.AddScoped<IBatterySavingsRepository, EfBatterySavingsRepository>();
        services.AddScoped<IOperationalEventRepository, EfOperationalEventRepository>();
        services.AddScoped<ILiveConsumptionRepository, EfLiveConsumptionRepository>();
        services.AddScoped<IWorkerFailureNotifier, WorkerFailureEmailNotifier>();
        services.AddSingleton<MqttTelemetrySnapshotStore>();
        services.AddScoped<IControllerSettingsService, ControllerSettingsService>();
        services.AddScoped<IBatteryConfigurationProvider, DatabaseBatteryConfigurationProvider>();
        services.AddScoped<ConfiguredBatteryStateProvider>();
        services.AddScoped<ConfiguredCurrentSiteTelemetryProvider>();
        services.AddScoped<MqttBatteryStateProvider>();
        services.AddScoped<MqttCurrentSiteTelemetryProvider>();
        services.AddScoped<ResilientBatteryStateProvider>();
        services.AddScoped<ResilientCurrentSiteTelemetryProvider>();
        services.AddHttpClient<HagerEnergyApiClient>();
        services.AddScoped<HagerEnergyBatteryStateProvider>();
        services.AddScoped<HagerEnergyCurrentSiteTelemetryProvider>();
        services.AddScoped<IBatteryStateProvider, SelectedBatteryStateProvider>();
        services.AddScoped<ICurrentSiteTelemetryProvider, SelectedCurrentSiteTelemetryProvider>();
        services.AddScoped<IHistoricalConsumptionProvider, AverageDailyConsumptionForecastProvider>();
        services.AddScoped<IBatteryForecastService, BatteryForecastService>();
        services.AddScoped(serviceProvider => new CurrentBatteryDecisionServiceDependencies
        {
            UtcClock = serviceProvider.GetRequiredService<IUtcClock>(),
            BatteryStateProvider = serviceProvider.GetRequiredService<IBatteryStateProvider>(),
            BatteryConfigurationProvider = serviceProvider.GetRequiredService<IBatteryConfigurationProvider>(),
            CurrentSiteTelemetryProvider = serviceProvider.GetRequiredService<ICurrentSiteTelemetryProvider>(),
            TibberPriceForecastProvider = serviceProvider.GetRequiredService<ITibberPriceForecastProvider>(),
            ControllerSettingStore = serviceProvider.GetRequiredService<IControllerSettingStore>(),
            DecisionLogRepository = serviceProvider.GetRequiredService<IDecisionLogRepository>()
        });
        services.AddScoped<ICurrentBatteryDecisionService, CurrentBatteryDecisionService>();
        services.AddHttpClient<ITibberPriceForecastProvider, TibberPriceForecastProvider>();
        services.AddSingleton<ForecastSolarPvForecastCache>();
        services.AddHttpClient<ForecastSolarPvForecastProvider>();
        services.AddScoped<IWeatherForecastProvider, CachedWeatherForecastProvider>();
        services.AddHostedService<DecisionExecutionBackgroundService>();
        services.AddHostedService<MqttTelemetryBackgroundService>();

        return services;
    }
}
