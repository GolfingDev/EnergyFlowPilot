using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Dal.E3dc;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
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
using TibberVictronController.Dal.Octopus;
using TibberVictronController.Dal.Prices;
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
        var dataProtection = services.AddDataProtection().SetApplicationName("EnergyFlowPilot");
        // Keep the key ring beside the database: both must be backed up together, and the service HOME
        // (default key location) is not part of the data directory.
        if (GetDataProtectionKeyDirectory(connectionString) is { } keyDirectory)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
        services.AddScoped<EfControllerSettingStore>();
        services.AddScoped<IControllerSettingStore>(provider => new ProtectedRscpSettingStore(
            provider.GetRequiredService<EfControllerSettingStore>(), provider.GetRequiredService<IDataProtectionProvider>(),
            provider.GetService<ILogger<ProtectedRscpSettingStore>>()));
        services.AddSingleton<E3dcRscpSession>();
        services.AddScoped<IE3dcTelemetrySource, E3dcRscpTelemetrySource>();
        services.AddScoped<IE3dcPowerControl, E3dcRscpPowerControl>();
        services.AddScoped<IJointBatteryHardware, JointBatteryHardware>();
        services.AddScoped<JointBatteryExecutionCoordinator>();
        services.AddSingleton<JointBatteryExecutionState>();
        services.AddScoped<JointBatteryRecoveryOverride>();
        services.AddScoped<JointManualChargeService>();
        services.AddSingleton<SystemdJointBatteryWatchdog>();
        services.AddSingleton<IJointBatteryWatchdog>(sp => sp.GetRequiredService<SystemdJointBatteryWatchdog>());
        services.AddHostedService<JointBatteryExecutionBackgroundService>();
        services.AddScoped<JointBatteryShadowCalculation>();
        services.AddSingleton<JointBatteryPriceHistory>();
        services.AddSingleton<JointBatteryRuntimeState>();
        services.AddHostedService<JointBatteryShadowBackgroundService>();
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
        services.AddScoped<WeishauptEnergyHistoryService>();
        services.AddScoped<TelemetrySourceSelector>();
        services.AddSingleton<VictronMqttRuntimeStatus>();
        services.AddSingleton<DecisionWorkerRuntimeStatus>();
        services.AddSingleton<DecisionCalculationTrigger>();
        services.AddSingleton<SignificantTelemetryChangeDetector>();
        services.AddSingleton<VictronSetpointRefreshState>();
        services.AddSingleton<SupplementaryGridChargeGuard>();
        services.AddScoped<ISupplementaryGridChargeSafety, SupplementaryGridChargeSafety>();
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
        services.AddScoped<BatteryForecastService>();
        services.AddScoped<IBatteryForecastService, JointAwareBatteryForecastService>();
        services.AddScoped(serviceProvider => new CurrentBatteryDecisionServiceDependencies
        {
            GridChargeGuard = serviceProvider.GetRequiredService<SupplementaryGridChargeGuard>(),
            UtcClock = serviceProvider.GetRequiredService<IUtcClock>(),
            BatteryStateProvider = serviceProvider.GetRequiredService<IBatteryStateProvider>(),
            BatteryConfigurationProvider = serviceProvider.GetRequiredService<IBatteryConfigurationProvider>(),
            CurrentSiteTelemetryProvider = serviceProvider.GetRequiredService<ICurrentSiteTelemetryProvider>(),
            TibberPriceForecastProvider = serviceProvider.GetRequiredService<IElectricityPriceForecastProvider>(),
            BatteryForecastService = serviceProvider.GetRequiredService<IBatteryForecastService>(),
            ControllerSettingStore = serviceProvider.GetRequiredService<IControllerSettingStore>(),
            DecisionLogRepository = serviceProvider.GetRequiredService<IDecisionLogRepository>()
        });
        services.AddScoped<CurrentBatteryDecisionService>();
        services.AddScoped<ICurrentBatteryDecisionService, JointAwareBatteryDecisionService>();
        services.AddScoped(serviceProvider => new DashboardSummaryServiceDependencies
        {
            UtcClock = serviceProvider.GetRequiredService<IUtcClock>(),
            DecisionLogRepository = serviceProvider.GetRequiredService<IDecisionLogRepository>(),
            LiveConsumptionRepository = serviceProvider.GetRequiredService<ILiveConsumptionRepository>(),
            BatteryForecastService = serviceProvider.GetRequiredService<IBatteryForecastService>(),
            BatteryConfigurationProvider = serviceProvider.GetRequiredService<IBatteryConfigurationProvider>(),
            TibberPriceForecastProvider = serviceProvider.GetRequiredService<IElectricityPriceForecastProvider>(),
            ControllerSettingStore = serviceProvider.GetRequiredService<IControllerSettingStore>()
        });
        services.AddScoped<IDashboardSummaryService, DashboardSummaryService>();
        services.AddSingleton<TibberPriceForecastCache>();
        services.AddHttpClient(nameof(TibberPriceForecastProvider));
        services.AddScoped<TibberPriceForecastProvider>(serviceProvider =>
        {
            var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

            return new TibberPriceForecastProvider(
                httpClientFactory.CreateClient(nameof(TibberPriceForecastProvider)),
                serviceProvider.GetRequiredService<IControllerSettingStore>(),
                serviceProvider.GetRequiredService<TibberPriceForecastCache>());
        });
        services.AddSingleton<OctopusPriceCache>();
        services.AddHttpClient<OctopusPriceForecastProvider>(client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<IElectricityPriceForecastProvider, SelectedElectricityPriceForecastProvider>();
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

    private static string? GetDataProtectionKeyDirectory(string connectionString)
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        // In-memory databases (tests) have no durable location; the framework default is sufficient there.
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:" ||
            dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            return null;
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        return string.IsNullOrEmpty(databaseDirectory) ? null : Path.Combine(databaseDirectory, "data-protection-keys");
    }
}
