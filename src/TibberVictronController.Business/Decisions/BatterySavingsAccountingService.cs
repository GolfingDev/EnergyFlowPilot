using System.Globalization;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Converts real measured live telemetry into durable daily battery savings summaries.
/// </summary>
public sealed class BatterySavingsAccountingService : IBatterySavingsAccountingService
{
    private const decimal MinimumAccountingPowerWatts = 25m;
    private static readonly TimeSpan MaximumSampleGap = TimeSpan.FromMinutes(5);

    private readonly ILiveConsumptionRepository liveConsumptionRepository;
    private readonly IBatterySavingsRepository batterySavingsRepository;
    private readonly ITibberPriceForecastProvider tibberPriceForecastProvider;
    private readonly IControllerSettingStore controllerSettingStore;
    private readonly IUtcClock utcClock;
    private readonly BatterySavingsCalculator calculator = new();

    public BatterySavingsAccountingService(
        ILiveConsumptionRepository liveConsumptionRepository,
        IBatterySavingsRepository batterySavingsRepository,
        ITibberPriceForecastProvider tibberPriceForecastProvider,
        IControllerSettingStore controllerSettingStore,
        IUtcClock utcClock)
    {
        this.liveConsumptionRepository = liveConsumptionRepository
            ?? throw new ArgumentNullException(nameof(liveConsumptionRepository));
        this.batterySavingsRepository = batterySavingsRepository
            ?? throw new ArgumentNullException(nameof(batterySavingsRepository));
        this.tibberPriceForecastProvider = tibberPriceForecastProvider
            ?? throw new ArgumentNullException(nameof(tibberPriceForecastProvider));
        this.controllerSettingStore = controllerSettingStore
            ?? throw new ArgumentNullException(nameof(controllerSettingStore));
        this.utcClock = utcClock
            ?? throw new ArgumentNullException(nameof(utcClock));
    }

    public async Task RefreshAsync(BatterySavingsQuery query, CancellationToken cancellationToken = default)
    {
        ValidateQuery(query);

        var reportingTimeZone = ResolveBerlinTimeZone();
        var startsAtUtc = ConvertLocalDateStartToUtc(query.StartDate, reportingTimeZone);
        var endsAtUtc = ConvertLocalDateStartToUtc(query.EndDate.AddDays(1), reportingTimeZone);
        var samples = await liveConsumptionRepository.GetSamplesAsync(startsAtUtc, endsAtUtc, cancellationToken);

        if (samples.Count < 2)
        {
            return;
        }

        var prices = await tibberPriceForecastProvider.GetPriceForecastAsync(startsAtUtc, endsAtUtc, cancellationToken);
        var relevantPrices = prices
            .Where(price => string.Equals(price.Currency, query.Currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(price => price.TimeSlot.StartsAtUtc)
            .ToArray();

        if (relevantPrices.Length == 0)
        {
            return;
        }

        var feedInCompensationPricePerKwh = await GetFeedInCompensationPricePerKwhAsync(cancellationToken);
        var movements = CreateMovements(samples, relevantPrices, feedInCompensationPricePerKwh, query.Currency);
        var summaries = calculator.CalculateDailySummaries(movements, new BatterySavingsCalculationOptions
        {
            ReportingTimeZone = reportingTimeZone,
            UpdatedAtUtc = utcClock.UtcNow
        });

        foreach (var summary in summaries)
        {
            if (summary.AccountingDate < query.StartDate || summary.AccountingDate > query.EndDate)
            {
                continue;
            }

            await batterySavingsRepository.SaveDailySummaryAsync(summary, cancellationToken);
        }
    }

    public async Task RefreshRecentDaysAsync(
        int dayCount,
        string currency,
        CancellationToken cancellationToken = default)
    {
        if (dayCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dayCount), "Es muss mindestens ein Tag aktualisiert werden.");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Die Waehrung muss angegeben werden.", nameof(currency));
        }

        var reportingTimeZone = ResolveBerlinTimeZone();
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcClock.UtcNow, reportingTimeZone).DateTime);
        var query = new BatterySavingsQuery
        {
            StartDate = localToday.AddDays(-(dayCount - 1)),
            EndDate = localToday,
            Currency = currency
        };

        await RefreshAsync(query, cancellationToken);
    }

    private static IReadOnlyList<BatterySavingsSlotMovement> CreateMovements(
        IReadOnlyList<LiveConsumptionSample> samples,
        IReadOnlyList<TibberPriceForecastSlot> prices,
        decimal feedInCompensationPricePerKwh,
        string currency)
    {
        var orderedSamples = samples
            .OrderBy(sample => sample.MeasuredAtUtc)
            .ToArray();
        var movements = new List<BatterySavingsSlotMovement>();

        for (var index = 1; index < orderedSamples.Length; index++)
        {
            var previous = orderedSamples[index - 1];
            var current = orderedSamples[index];
            var duration = current.MeasuredAtUtc - previous.MeasuredAtUtc;

            if (duration <= TimeSpan.Zero || duration > MaximumSampleGap)
            {
                continue;
            }

            if (previous.BatteryPowerWatts is null || current.BatteryPowerWatts is null)
            {
                continue;
            }

            if (Math.Sign(previous.BatteryPowerWatts.Value) != Math.Sign(current.BatteryPowerWatts.Value))
            {
                continue;
            }

            var averageBatteryPowerWatts = (previous.BatteryPowerWatts.Value + current.BatteryPowerWatts.Value) / 2m;
            if (Math.Abs(averageBatteryPowerWatts) < MinimumAccountingPowerWatts)
            {
                continue;
            }

            var price = FindPrice(prices, previous.MeasuredAtUtc.AddTicks(duration.Ticks / 2));
            if (price is null)
            {
                continue;
            }

            var timeSlot = new ForecastTimeSlot(previous.MeasuredAtUtc, current.MeasuredAtUtc);
            if (averageBatteryPowerWatts < 0m)
            {
                movements.Add(CreateMovement(
                    timeSlot,
                    BatteryDecisionState.Discharge,
                    chargeSource: null,
                    Math.Abs(averageBatteryPowerWatts),
                    price.TotalPricePerKwh,
                    currency,
                    feedInCompensationPricePerKwh));
                continue;
            }

            var chargePowerWatts = averageBatteryPowerWatts;
            var pvChargePowerWatts = EstimatePvChargePowerWatts(previous, current, chargePowerWatts);
            var gridChargePowerWatts = Math.Max(0m, chargePowerWatts - pvChargePowerWatts);

            AddChargeMovementIfRelevant(
                movements,
                timeSlot,
                BatteryChargeSource.PV,
                pvChargePowerWatts,
                price.TotalPricePerKwh,
                currency,
                feedInCompensationPricePerKwh);
            AddChargeMovementIfRelevant(
                movements,
                timeSlot,
                BatteryChargeSource.Grid,
                gridChargePowerWatts,
                price.TotalPricePerKwh,
                currency,
                feedInCompensationPricePerKwh);
        }

        return movements;
    }

    private static decimal EstimatePvChargePowerWatts(
        LiveConsumptionSample previous,
        LiveConsumptionSample current,
        decimal chargePowerWatts)
    {
        var averagePvProductionWatts = AverageNullable(previous.PvProductionWatts, current.PvProductionWatts);
        var averageHouseConsumptionWatts = (previous.HouseConsumptionWatts + current.HouseConsumptionWatts) / 2m;

        if (averagePvProductionWatts is not null)
        {
            var pvSurplusWatts = Math.Max(0m, averagePvProductionWatts.Value - Math.Max(0m, averageHouseConsumptionWatts));

            return Math.Min(chargePowerWatts, pvSurplusWatts);
        }

        var averageGridPowerWatts = AverageNullable(previous.GridPowerWatts, current.GridPowerWatts);
        if (averageGridPowerWatts is null)
        {
            return 0m;
        }

        var gridChargePowerWatts = Math.Min(chargePowerWatts, Math.Max(0m, averageGridPowerWatts.Value));

        return Math.Max(0m, chargePowerWatts - gridChargePowerWatts);
    }

    private static void AddChargeMovementIfRelevant(
        List<BatterySavingsSlotMovement> movements,
        ForecastTimeSlot timeSlot,
        BatteryChargeSource chargeSource,
        decimal powerWatts,
        decimal tibberPricePerKwh,
        string currency,
        decimal feedInCompensationPricePerKwh)
    {
        if (powerWatts < MinimumAccountingPowerWatts)
        {
            return;
        }

        movements.Add(CreateMovement(
            timeSlot,
            BatteryDecisionState.Charge,
            chargeSource,
            powerWatts,
            tibberPricePerKwh,
            currency,
            feedInCompensationPricePerKwh));
    }

    private static BatterySavingsSlotMovement CreateMovement(
        ForecastTimeSlot timeSlot,
        BatteryDecisionState decisionState,
        BatteryChargeSource? chargeSource,
        decimal powerWatts,
        decimal tibberPricePerKwh,
        string currency,
        decimal feedInCompensationPricePerKwh)
    {
        return new BatterySavingsSlotMovement
        {
            TimeSlot = timeSlot,
            Instruction = new BatteryDecisionInstruction(decisionState, chargeSource),
            TargetPowerWatts = (int)Math.Round(powerWatts, MidpointRounding.AwayFromZero),
            TibberPricePerKwh = tibberPricePerKwh,
            Currency = currency,
            PvSalePricePerKwh = feedInCompensationPricePerKwh
        };
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

        if (!decimal.TryParse(setting.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var pricePerKwh))
        {
            throw new InvalidOperationException("Die Einspeiseverguetung muss als Dezimalzahl konfiguriert sein.");
        }

        if (pricePerKwh < 0m)
        {
            throw new InvalidOperationException("Die Einspeiseverguetung darf nicht negativ sein.");
        }

        return pricePerKwh;
    }

    private static TibberPriceForecastSlot? FindPrice(
        IReadOnlyList<TibberPriceForecastSlot> prices,
        DateTimeOffset measuredAtUtc)
    {
        return prices.FirstOrDefault(price =>
            price.TimeSlot.StartsAtUtc <= measuredAtUtc &&
            price.TimeSlot.EndsAtUtc > measuredAtUtc);
    }

    private static decimal? AverageNullable(decimal? first, decimal? second)
    {
        if (first is not null && second is not null)
        {
            return (first.Value + second.Value) / 2m;
        }

        return first ?? second;
    }

    private static DateTimeOffset ConvertLocalDateStartToUtc(DateOnly localDate, TimeZoneInfo timeZone)
    {
        var localDateTime = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, timeZone);

        return new DateTimeOffset(utcDateTime, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveBerlinTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }

    private static void ValidateQuery(BatterySavingsQuery query)
    {
        if (query is null)
        {
            throw new ArgumentNullException(nameof(query), "Die Batterie-Ersparnis-Abfrage darf nicht null sein.");
        }

        if (query.EndDate < query.StartDate)
        {
            throw new ArgumentException("Das Enddatum muss nach dem Startdatum liegen.", nameof(query));
        }

        if (string.IsNullOrWhiteSpace(query.Currency))
        {
            throw new ArgumentException("Die Waehrung muss angegeben werden.", nameof(query));
        }
    }
}
