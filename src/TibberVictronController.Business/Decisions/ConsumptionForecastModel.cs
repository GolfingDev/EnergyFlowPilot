using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Average measured consumption of one local day and 15-minute slot.
/// </summary>
public sealed record ConsumptionDaySlotAverage(DateOnly LocalDate, int SlotIndex, decimal AverageWatts);

/// <summary>
/// Measured energy and heating degrees of one complete local day; stored beyond the raw sample retention.
/// </summary>
public sealed record ConsumptionDailySummary(DateOnly LocalDate, decimal EnergyKwh, decimal HeatingDegrees, int SlotCount = ConsumptionForecastModel.SlotsPerDay);

/// <summary>
/// Learns a daily consumption shape and a temperature dependency from recent measured days.
/// </summary>
/// <remarks>
/// The shape uses all recent days instead of one weekday, because recurring loads such as a heat pump follow a daily routine
/// and a weekday profile only has two samples per slot within two weeks. Newer days weigh more (half-life of three days) so
/// seasonal changes show up quickly. The daily energy is regressed against heating degrees (mean of max(0, 15 °C - T)), and
/// each forecast day is scaled by its expected heating degrees relative to the days the shape was learned from.
/// The raw samples are only kept for 14 days, in which the temperature hardly changes. Stored daily summaries extend the
/// regression over all seasons, so the heat pump's demand at frost or the base load in summer is learned once and kept.
/// </remarks>
public sealed class ConsumptionForecastModel
{
    public const int SlotsPerDay = 96;
    private const decimal HeatingBaseTemperatureCelsius = 15m;
    private const double RecencyHalfLifeDays = 3d;
    private const int MinimumCompleteDaysForRegression = 5;
    private const int MinimumSlotsForCompleteDay = 90;
    private const decimal MinimumScale = 0.5m;
    private const decimal MaximumScale = 2.0m;
    private const double MinimumCorrelation = 0.3d;

    private readonly IReadOnlyDictionary<int, decimal> profileWattsBySlot;
    private readonly IReadOnlyDictionary<DateOnly, decimal> heatingDegreesByDate;
    private readonly decimal referenceHeatingDegrees;

    private ConsumptionForecastModel(
        IReadOnlyDictionary<int, decimal> profileWattsBySlot,
        IReadOnlyDictionary<DateOnly, decimal> heatingDegreesByDate,
        decimal referenceHeatingDegrees,
        decimal? baseDailyKwh,
        decimal? heatingSlopeKwhPerDegree,
        int completeDays)
    {
        this.profileWattsBySlot = profileWattsBySlot;
        this.heatingDegreesByDate = heatingDegreesByDate;
        this.referenceHeatingDegrees = referenceHeatingDegrees;
        BaseDailyKwh = baseDailyKwh;
        HeatingSlopeKwhPerDegree = heatingSlopeKwhPerDegree;
        CompleteDays = completeDays;
    }

    /// <summary>
    /// Gets the regression intercept in kWh per day, or null when no temperature dependency was learned.
    /// </summary>
    public decimal? BaseDailyKwh { get; }

    /// <summary>
    /// Gets the additional daily consumption per heating degree, or null when no temperature dependency was learned.
    /// </summary>
    public decimal? HeatingSlopeKwhPerDegree { get; }

    public int CompleteDays { get; }

    public bool HasProfile => profileWattsBySlot.Count > 0;

    /// <summary>
    /// Builds the model from per-day slot averages and the hourly temperatures converted to heating degrees per local date.
    /// </summary>
    public static ConsumptionForecastModel Build(
        IReadOnlyList<ConsumptionDaySlotAverage> history,
        IReadOnlyDictionary<DateOnly, decimal> heatingDegreesByDate,
        DateOnly today,
        IReadOnlyList<ConsumptionDailySummary>? storedDays = null)
    {
        double Weight(DateOnly date) => Math.Pow(0.5, Math.Max(0, today.DayNumber - date.DayNumber) / RecencyHalfLifeDays);

        var profile = history
            .GroupBy(average => average.SlotIndex)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var weightSum = group.Sum(average => Weight(average.LocalDate));
                    return weightSum <= 0d
                        ? 0m
                        : (decimal)(group.Sum(average => (double)average.AverageWatts * Weight(average.LocalDate)) / weightSum);
                });

        // Recent days come from the raw samples; stored days fill in everything older. Recent values win for the same date.
        var regressionDays = (storedDays ?? Array.Empty<ConsumptionDailySummary>())
            .ToDictionary(day => day.LocalDate);
        foreach (var recentDay in SummarizeCompleteDays(history, heatingDegreesByDate, today))
        {
            regressionDays[recentDay.LocalDate] = recentDay;
        }

        var completeDays = regressionDays.Values.ToArray();

        var profileDates = history.Select(average => average.LocalDate).Distinct().Where(heatingDegreesByDate.ContainsKey).ToArray();
        var referenceWeightSum = profileDates.Sum(Weight);
        var referenceHeatingDegrees = referenceWeightSum <= 0d
            ? 0m
            : (decimal)(profileDates.Sum(date => (double)heatingDegreesByDate[date] * Weight(date)) / referenceWeightSum);

        decimal? intercept = null;
        decimal? slope = null;

        if (completeDays.Length >= MinimumCompleteDaysForRegression)
        {
            var meanX = completeDays.Average(day => day.HeatingDegrees);
            var meanY = completeDays.Average(day => day.EnergyKwh);
            var covariance = completeDays.Sum(day => (day.HeatingDegrees - meanX) * (day.EnergyKwh - meanY));
            var variance = completeDays.Sum(day => (day.HeatingDegrees - meanX) * (day.HeatingDegrees - meanX));
            var energyVariance = completeDays.Sum(day => (day.EnergyKwh - meanY) * (day.EnergyKwh - meanY));
            var correlation = variance > 0m && energyVariance > 0m
                ? (double)covariance / Math.Sqrt((double)variance * (double)energyVariance)
                : 0d;

            // Only a clearly positive heating dependency is used; noise or warm weeks without heating keep the plain profile.
            if (variance > 0.5m && correlation >= MinimumCorrelation)
            {
                slope = covariance / variance;
                intercept = meanY - slope * meanX;
            }
        }

        return new ConsumptionForecastModel(profile, heatingDegreesByDate, referenceHeatingDegrees, intercept, slope, completeDays.Length);
    }

    /// <summary>
    /// Returns energy and heating degrees of every complete past day in the slot history that has temperatures.
    /// </summary>
    public static IReadOnlyList<ConsumptionDailySummary> SummarizeCompleteDays(
        IReadOnlyList<ConsumptionDaySlotAverage> history,
        IReadOnlyDictionary<DateOnly, decimal> heatingDegreesByDate,
        DateOnly today)
    {
        return history
            .GroupBy(average => average.LocalDate)
            .Where(group => group.Count() >= MinimumSlotsForCompleteDay && group.Key < today && heatingDegreesByDate.ContainsKey(group.Key))
            .Select(group => new ConsumptionDailySummary(
                group.Key,
                group.Average(average => average.AverageWatts) * 24m / 1000m,
                heatingDegreesByDate[group.Key],
                group.Count()))
            .OrderBy(day => day.LocalDate)
            .ToArray();
    }

    /// <summary>
    /// Converts hourly temperatures into heating degrees per local date (mean of max(0, 15 °C - T) over the available hours).
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, decimal> CalculateHeatingDegrees(
        IReadOnlyList<HourlyTemperature> temperatures,
        TimeZoneInfo timeZone)
    {
        return temperatures
            .GroupBy(temperature => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(temperature.StartsAtUtc, timeZone).DateTime))
            .Where(group => group.Count() >= 20)
            .ToDictionary(
                group => group.Key,
                group => group.Average(temperature => Math.Max(0m, HeatingBaseTemperatureCelsius - temperature.TemperatureCelsius)));
    }

    /// <summary>
    /// Returns the expected consumption of one slot in kWh, or null when no measured profile exists for it.
    /// </summary>
    public decimal? GetSlotConsumptionKwh(DateOnly localDate, int slotIndex, TimeSpan slotDuration)
    {
        if (!profileWattsBySlot.TryGetValue(slotIndex, out var profileWatts))
        {
            return null;
        }

        return profileWatts * GetTemperatureScale(localDate) / 1000m * (decimal)slotDuration.TotalHours;
    }

    /// <summary>
    /// Returns how much more or less the given day is expected to consume than the days the shape was learned from.
    /// </summary>
    public decimal GetTemperatureScale(DateOnly localDate)
    {
        if (BaseDailyKwh is not { } intercept ||
            HeatingSlopeKwhPerDegree is not { } slope ||
            !heatingDegreesByDate.TryGetValue(localDate, out var heatingDegrees))
        {
            return 1m;
        }

        var referenceKwh = intercept + slope * referenceHeatingDegrees;
        if (referenceKwh <= 0m)
        {
            return 1m;
        }

        return Math.Clamp((intercept + slope * heatingDegrees) / referenceKwh, MinimumScale, MaximumScale);
    }
}
