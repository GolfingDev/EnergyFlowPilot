using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Loads all forecast inputs and delegates slotwise calculation to the Decision Engine forecast simulator.
/// </summary>
public sealed class BatteryForecastService : IBatteryForecastService
{
    private readonly ITibberPriceForecastProvider tibberPriceForecastProvider;
    private readonly IWeatherForecastProvider weatherForecastProvider;
    private readonly IHistoricalConsumptionProvider historicalConsumptionProvider;
    private readonly IBatteryStateProvider batteryStateProvider;
    private readonly IBatteryConfigurationProvider batteryConfigurationProvider;
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly BatteryForecastSimulator batteryForecastSimulator;

    public BatteryForecastService(
        ITibberPriceForecastProvider tibberPriceForecastProvider,
        IWeatherForecastProvider weatherForecastProvider,
        IHistoricalConsumptionProvider historicalConsumptionProvider,
        IBatteryStateProvider batteryStateProvider,
        IBatteryConfigurationProvider batteryConfigurationProvider,
        IControllerSettingStore controllerSettingStore)
        : this(
            tibberPriceForecastProvider,
            weatherForecastProvider,
            historicalConsumptionProvider,
            batteryStateProvider,
            batteryConfigurationProvider,
            controllerSettingStore,
            new BatteryForecastSimulator())
    {
    }

    internal BatteryForecastService(
        ITibberPriceForecastProvider tibberPriceForecastProvider,
        IWeatherForecastProvider weatherForecastProvider,
        IHistoricalConsumptionProvider historicalConsumptionProvider,
        IBatteryStateProvider batteryStateProvider,
        IBatteryConfigurationProvider batteryConfigurationProvider,
        IControllerSettingStore controllerSettingStore,
        BatteryForecastSimulator batteryForecastSimulator)
    {
        this.tibberPriceForecastProvider = tibberPriceForecastProvider
            ?? throw new ArgumentNullException(nameof(tibberPriceForecastProvider), "Der Tibber-Preisprovider darf nicht null sein.");
        this.weatherForecastProvider = weatherForecastProvider
            ?? throw new ArgumentNullException(nameof(weatherForecastProvider), "Der Wetter-/PV-Provider darf nicht null sein.");
        this.historicalConsumptionProvider = historicalConsumptionProvider
            ?? throw new ArgumentNullException(nameof(historicalConsumptionProvider), "Der Verbrauchsforecast-Provider darf nicht null sein.");
        this.batteryStateProvider = batteryStateProvider
            ?? throw new ArgumentNullException(nameof(batteryStateProvider), "Der Akkustandsprovider darf nicht null sein.");
        this.batteryConfigurationProvider = batteryConfigurationProvider
            ?? throw new ArgumentNullException(nameof(batteryConfigurationProvider), "Der Batteriekonfigurationsprovider darf nicht null sein.");
        this.controllerSettingStore = controllerSettingStore
            ?? throw new ArgumentNullException(nameof(controllerSettingStore), "Der Einstellungsspeicher darf nicht null sein.");
        this.batteryForecastSimulator = batteryForecastSimulator
            ?? throw new ArgumentNullException(nameof(batteryForecastSimulator), "Der Forecast-Simulator darf nicht null sein.");
    }

    /// <summary>
    /// Loads all required forecast inputs for the UTC range and returns a slotwise Decision Engine forecast.
    /// </summary>
    public async Task<BatteryForecastResult> CalculateForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateUtcRange(startsAtUtc, endsAtUtc);

        var priceForecast = await tibberPriceForecastProvider.GetPriceForecastAsync(startsAtUtc, endsAtUtc, cancellationToken);
        var batteryState = await batteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        var batteryConfiguration = await batteryConfigurationProvider.GetBatteryConfigurationAsync(cancellationToken);
        var relevantPriceForecast = priceForecast
            .Where(priceSlot =>
                priceSlot.TimeSlot.StartsAtUtc >= startsAtUtc &&
                priceSlot.TimeSlot.EndsAtUtc <= endsAtUtc)
            .OrderBy(priceSlot => priceSlot.TimeSlot.StartsAtUtc)
            .ToArray();

        if (relevantPriceForecast.Length == 0)
        {
            return new BatteryForecastResult(
                batteryState,
                batteryConfiguration,
                Array.Empty<BatteryForecastEntry>());
        }

        var effectiveEndsAtUtc = relevantPriceForecast[^1].TimeSlot.EndsAtUtc;
        var feedInCompensationPricePerKwh = await GetFeedInCompensationPricePerKwhAsync(cancellationToken);
        var pvForecast = await weatherForecastProvider.GetPvYieldForecastAsync(startsAtUtc, effectiveEndsAtUtc, cancellationToken);
        var consumptionForecast = await historicalConsumptionProvider.GetConsumptionForecastAsync(startsAtUtc, effectiveEndsAtUtc, cancellationToken);

        return batteryForecastSimulator.Simulate(
            relevantPriceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh);
    }

    private async Task<decimal> GetFeedInCompensationPricePerKwhAsync(CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(
            ControllerSettingDefaults.GridFeedInCompensationPricePerKwhKey,
            cancellationToken);

        if (setting is null || !setting.IsConfigured)
        {
            throw new InvalidOperationException("Die Einspeiseverguetung ist nicht konfiguriert.");
        }

        if (!decimal.TryParse(setting.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var feedInCompensationPricePerKwh))
        {
            throw new InvalidOperationException("Die Einspeiseverguetung muss als Dezimalzahl konfiguriert sein.");
        }

        if (feedInCompensationPricePerKwh < 0m)
        {
            throw new InvalidOperationException("Die Einspeiseverguetung darf nicht negativ sein.");
        }

        return feedInCompensationPricePerKwh;
    }

    private static void ValidateUtcRange(DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        if (startsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Forecast-Start muss in UTC angegeben sein.", nameof(startsAtUtc));
        }

        if (endsAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Das Forecast-Ende muss in UTC angegeben sein.", nameof(endsAtUtc));
        }

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("Das Forecast-Ende muss nach dem Forecast-Start liegen.", nameof(endsAtUtc));
        }
    }
}
