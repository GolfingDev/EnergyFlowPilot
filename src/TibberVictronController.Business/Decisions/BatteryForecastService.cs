using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Loads all forecast inputs and delegates slotwise calculation to the Decision Engine forecast simulator.
/// </summary>
public sealed class BatteryForecastService : IBatteryForecastService
{
    private readonly IElectricityPriceForecastProvider tibberPriceForecastProvider;
    private readonly IWeatherForecastProvider weatherForecastProvider;
    private readonly IHistoricalConsumptionProvider historicalConsumptionProvider;
    private readonly IBatteryStateProvider batteryStateProvider;
    private readonly IBatteryConfigurationProvider batteryConfigurationProvider;
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly BatteryForecastSimulator batteryForecastSimulator;
    private readonly IDecisionLogRepository? decisionLogRepository;
    private readonly IPriceDriverProvider? priceDriverProvider;

    public BatteryForecastService(
        IElectricityPriceForecastProvider tibberPriceForecastProvider,
        IWeatherForecastProvider weatherForecastProvider,
        IHistoricalConsumptionProvider historicalConsumptionProvider,
        IBatteryStateProvider batteryStateProvider,
        IBatteryConfigurationProvider batteryConfigurationProvider,
        IControllerSettingStore controllerSettingStore,
        IDecisionLogRepository? decisionLogRepository = null,
        IPriceDriverProvider? priceDriverProvider = null)
        : this(
            tibberPriceForecastProvider,
            weatherForecastProvider,
            historicalConsumptionProvider,
            batteryStateProvider,
            batteryConfigurationProvider,
            controllerSettingStore,
            new BatteryForecastSimulator())
    {
        this.decisionLogRepository = decisionLogRepository;
        this.priceDriverProvider = priceDriverProvider;
    }

    internal BatteryForecastService(
        IElectricityPriceForecastProvider tibberPriceForecastProvider,
        IWeatherForecastProvider weatherForecastProvider,
        IHistoricalConsumptionProvider historicalConsumptionProvider,
        IBatteryStateProvider batteryStateProvider,
        IBatteryConfigurationProvider batteryConfigurationProvider,
        IControllerSettingStore controllerSettingStore,
        BatteryForecastSimulator batteryForecastSimulator)
    {
        this.tibberPriceForecastProvider = tibberPriceForecastProvider
            ?? throw new ArgumentNullException(nameof(tibberPriceForecastProvider), "Der Strompreisprovider darf nicht null sein.");
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
    public Task<BatteryForecastResult> CalculateForecastAsync(
        DateTimeOffset startsAtUtc,
        DateTimeOffset endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        return CalculateForecastAsync(new BatteryForecastRequest { StartsAtUtc = startsAtUtc, EndsAtUtc = endsAtUtc }, cancellationToken);
    }

    public async Task<BatteryForecastResult> CalculateForecastAsync(
        BatteryForecastRequest request,
        CancellationToken cancellationToken = default)
    {
        var startsAtUtc = request.StartsAtUtc;
        var endsAtUtc = request.EndsAtUtc;
        ValidateUtcRange(startsAtUtc, endsAtUtc);

        var priceForecast = await tibberPriceForecastProvider.GetPriceForecastAsync(startsAtUtc, endsAtUtc, cancellationToken);
        var batteryState = await batteryStateProvider.GetCurrentBatteryStateAsync(cancellationToken);
        var batteryConfiguration = await batteryConfigurationProvider.GetBatteryConfigurationAsync(cancellationToken);
        IReadOnlyList<TibberPriceForecastSlot> relevantPriceForecast = priceForecast
            .Where(priceSlot =>
                priceSlot.TimeSlot.StartsAtUtc >= startsAtUtc &&
                priceSlot.TimeSlot.EndsAtUtc <= endsAtUtc)
            .OrderBy(priceSlot => priceSlot.TimeSlot.StartsAtUtc)
            .ToArray();

        if (decisionLogRepository is not null)
        {
            var timeZone = await GetForecastTimeZoneAsync(cancellationToken);
            var estimator = new ComparableDayPriceEstimator(
                tibberPriceForecastProvider,
                decisionLogRepository,
                timeZone);
            relevantPriceForecast = await estimator.CompleteAsync(relevantPriceForecast, endsAtUtc, DateTimeOffset.UtcNow, cancellationToken);

            if (priceDriverProvider is not null)
            {
                try
                {
                    relevantPriceForecast = await new WeatherPriceEstimator(priceDriverProvider, timeZone)
                        .RefineAsync(relevantPriceForecast, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The weather estimate only refines the comparable-day estimate; a failure must never break the plan.
                }
            }
        }

        if (relevantPriceForecast.Count == 0)
        {
            return new BatteryForecastResult(
                batteryState,
                batteryConfiguration,
                Array.Empty<BatteryForecastEntry>());
        }

        var effectiveStartsAtUtc = relevantPriceForecast[0].TimeSlot.StartsAtUtc;
        var effectiveEndsAtUtc = relevantPriceForecast[^1].TimeSlot.EndsAtUtc;
        var feedInCompensationPricePerKwh = await GetFeedInCompensationPricePerKwhAsync(cancellationToken);
        var pvForecast = await weatherForecastProvider.GetPvYieldForecastAsync(effectiveStartsAtUtc, effectiveEndsAtUtc, cancellationToken);
        var consumptionForecast = await historicalConsumptionProvider.GetConsumptionForecastAsync(effectiveStartsAtUtc, effectiveEndsAtUtc, cancellationToken);

        if (request.CurrentNetLoadWatts is { } currentNetLoadWatts)
        {
            consumptionForecast = ApplyCurrentNetLoad(consumptionForecast, pvForecast, effectiveStartsAtUtc, currentNetLoadWatts);
        }

        return batteryForecastSimulator.Simulate(
            relevantPriceForecast,
            pvForecast,
            consumptionForecast,
            batteryState,
            batteryConfiguration,
            feedInCompensationPricePerKwh);
    }

    /// <summary>
    /// Replaces the expected consumption of the first slot so that consumption minus forecast PV equals the live net load.
    /// </summary>
    /// <remarks>
    /// The live value already contains the real PV production, so the forecast PV is added back to keep the simulator's
    /// "consumption - PV" equal to what is measured. The decision runs every few seconds, so a short load peak only affects
    /// the plan while it lasts.
    /// </remarks>
    public static IReadOnlyList<ConsumptionForecastSlot> ApplyCurrentNetLoad(
        IReadOnlyList<ConsumptionForecastSlot> consumptionForecast,
        IReadOnlyList<PvYieldForecastSlot> pvForecast,
        DateTimeOffset currentSlotStartsAtUtc,
        decimal currentNetLoadWatts)
    {
        return consumptionForecast
            .Select(slot =>
            {
                if (slot.TimeSlot.StartsAtUtc != currentSlotStartsAtUtc)
                {
                    return slot;
                }

                var pvYieldKwh = pvForecast.FirstOrDefault(pvSlot => pvSlot.TimeSlot == slot.TimeSlot)?.ExpectedPvYieldKwh ?? 0m;
                var netLoadKwh = currentNetLoadWatts / 1000m * (decimal)slot.TimeSlot.Duration.TotalHours;

                return new ConsumptionForecastSlot(slot.TimeSlot, Math.Max(0m, netLoadKwh + pvYieldKwh));
            })
            .ToArray();
    }

    private async Task<TimeZoneInfo> GetForecastTimeZoneAsync(CancellationToken cancellationToken)
    {
        var setting = await controllerSettingStore.GetSettingAsync(ControllerSettingDefaults.ConsumptionForecastTimeZoneKey, cancellationToken);
        var timeZoneId = string.IsNullOrWhiteSpace(setting?.Value) ? "Europe/Berlin" : setting.Value;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Europe/Berlin", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
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
