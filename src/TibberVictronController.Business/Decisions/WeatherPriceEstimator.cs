using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>
/// Replaces comparable-day price estimates with an estimate from the weather over Germany.
/// </summary>
/// <remarks>
/// The German day-ahead price follows the residual load: wind in the north and sun in the south push expensive plants out
/// of the market. A linear model learns the spot price from a wind index, the irradiance, the local hour and the weekend
/// over the last weeks. Tested on a held-out week (2026-10-11 to 10-18), the error was 43 EUR/MWh instead of 66 EUR/MWh
/// for the comparable day, and 91 % of hour pairs more than 2 ct apart were ordered correctly. That order decides which
/// slots the plan holds energy for.
/// The Tibber end price (grid fees, taxes, VAT) is a linear function of the spot price; it is calibrated on the slots
/// whose Tibber price is already published. Without inputs, model or calibration the comparable-day estimate stays.
/// </remarks>
public sealed class WeatherPriceEstimator
{
    private const int MinimumTrainingHours = 168;
    private const int MinimumCalibrationSlots = 16;
    private const double RidgePenalty = 1e-3d;

    private static readonly object ModelCacheLock = new();
    private static (DateTimeOffset LoadedAtUtc, string TimeZoneId, WeatherSpotPriceModel? Model)? modelCache;

    private readonly IPriceDriverProvider priceDriverProvider;
    private readonly TimeZoneInfo timeZone;

    public WeatherPriceEstimator(IPriceDriverProvider priceDriverProvider, TimeZoneInfo timeZone)
    {
        this.priceDriverProvider = priceDriverProvider;
        this.timeZone = timeZone;
    }

    /// <summary>
    /// Replaces the prices of estimated slots with the weather-based estimate where weather and calibration are available.
    /// </summary>
    public async Task<IReadOnlyList<TibberPriceForecastSlot>> RefineAsync(
        IReadOnlyList<TibberPriceForecastSlot> slots,
        CancellationToken cancellationToken = default)
    {
        if (!slots.Any(slot => slot.IsEstimated))
        {
            return slots;
        }

        var data = await priceDriverProvider.GetPriceDriversAsync(cancellationToken);
        if (data is null)
        {
            return slots;
        }

        var model = GetOrFitModel(data, timeZone);
        if (model is null)
        {
            return slots;
        }

        // Spot prices may come in 15-minute or hourly resolution; slots without an exact match use their hour.
        var spotBySlot = data.SpotPrices
            .GroupBy(spot => spot.StartsAtUtc)
            .ToDictionary(group => group.Key, group => group.First().PricePerMwh);
        foreach (var hour in data.SpotPrices.GroupBy(spot => FloorToHour(spot.StartsAtUtc)))
        {
            for (var offset = 0; offset < 4; offset++)
            {
                spotBySlot.TryAdd(hour.Key.AddMinutes(15 * offset), hour.Average(spot => spot.PricePerMwh));
            }
        }

        if (!TryCalibrate(slots, spotBySlot, out var calibration))
        {
            return slots;
        }

        var weatherByHour = data.Weather.ToDictionary(hour => hour.StartsAtUtc);

        return slots
            .Select(slot =>
            {
                if (!slot.IsEstimated || !weatherByHour.TryGetValue(FloorToHour(slot.TimeSlot.StartsAtUtc), out var weather))
                {
                    return slot;
                }

                var spotPerMwh = model.Predict(weather, timeZone);
                var pricePerKwh = calibration.Intercept + calibration.Slope * (decimal)spotPerMwh;

                // Negative-price rules must only act on published prices, as for the comparable day.
                return new TibberPriceForecastSlot(slot.TimeSlot, Math.Round(Math.Max(0m, pricePerKwh), 4), slot.Currency)
                {
                    IsEstimated = true
                };
            })
            .ToArray();
    }

    /// <summary>
    /// Fits the spot price model on hourly spot prices and weather; returns null without enough overlapping hours.
    /// </summary>
    public static WeatherSpotPriceModel? Fit(PriceDriverData data, TimeZoneInfo timeZone)
    {
        var weatherByHour = data.Weather.ToDictionary(hour => hour.StartsAtUtc);
        var rows = new List<double[]>();
        var targets = new List<double>();

        foreach (var hour in data.SpotPrices.GroupBy(spot => FloorToHour(spot.StartsAtUtc)))
        {
            if (weatherByHour.TryGetValue(hour.Key, out var weather))
            {
                rows.Add(WeatherSpotPriceModel.CreateFeatures(weather, timeZone));
                targets.Add((double)hour.Average(spot => spot.PricePerMwh));
            }
        }

        return rows.Count < MinimumTrainingHours
            ? null
            : new WeatherSpotPriceModel(SolveRidge(rows, targets));
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset value)
    {
        return new DateTimeOffset(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
    }

    private static WeatherSpotPriceModel? GetOrFitModel(PriceDriverData data, TimeZoneInfo timeZone)
    {
        lock (ModelCacheLock)
        {
            if (modelCache is { } cached && cached.LoadedAtUtc == data.LoadedAtUtc && cached.TimeZoneId == timeZone.Id)
            {
                return cached.Model;
            }
        }

        var model = Fit(data, timeZone);

        lock (ModelCacheLock)
        {
            modelCache = (data.LoadedAtUtc, timeZone.Id, model);
        }

        return model;
    }

    /// <summary>
    /// Fits Tibber price = intercept + slope x spot price on published slots (grid fees, taxes and VAT).
    /// </summary>
    private static bool TryCalibrate(
        IReadOnlyList<TibberPriceForecastSlot> slots,
        IReadOnlyDictionary<DateTimeOffset, decimal> spotBySlot,
        out (decimal Intercept, decimal Slope) calibration)
    {
        calibration = default;
        var pairs = slots
            .Where(slot => !slot.IsEstimated && spotBySlot.ContainsKey(slot.TimeSlot.StartsAtUtc))
            .Select(slot => (Spot: spotBySlot[slot.TimeSlot.StartsAtUtc], Price: slot.TotalPricePerKwh))
            .ToArray();

        if (pairs.Length < MinimumCalibrationSlots)
        {
            return false;
        }

        var meanSpot = pairs.Average(pair => pair.Spot);
        var meanPrice = pairs.Average(pair => pair.Price);
        var variance = pairs.Sum(pair => (pair.Spot - meanSpot) * (pair.Spot - meanSpot));

        // Without spread in the spot price the slope is undefined; a flat day cannot calibrate the conversion.
        if (variance <= 0m)
        {
            return false;
        }

        var slope = pairs.Sum(pair => (pair.Spot - meanSpot) * (pair.Price - meanPrice)) / variance;
        if (slope <= 0m)
        {
            return false;
        }

        calibration = (meanPrice - slope * meanSpot, slope);
        return true;
    }

    private static double[] SolveRidge(IReadOnlyList<double[]> rows, IReadOnlyList<double> targets)
    {
        var size = rows[0].Length;
        var matrix = new double[size, size];
        var vector = new double[size];

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (var i = 0; i < size; i++)
            {
                vector[i] += row[i] * targets[rowIndex];
                for (var j = 0; j < size; j++)
                {
                    matrix[i, j] += row[i] * row[j];
                }
            }
        }

        // A small ridge keeps rarely seen hour dummies stable; the intercept is not penalised.
        for (var i = 1; i < size; i++)
        {
            matrix[i, i] += RidgePenalty * rows.Count;
        }

        for (var column = 0; column < size; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }

            for (var j = 0; j < size; j++)
            {
                (matrix[column, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[column, j]);
            }

            (vector[column], vector[pivot]) = (vector[pivot], vector[column]);

            for (var row = 0; row < size; row++)
            {
                if (row == column || matrix[column, column] == 0d)
                {
                    continue;
                }

                var factor = matrix[row, column] / matrix[column, column];
                for (var j = 0; j < size; j++)
                {
                    matrix[row, j] -= factor * matrix[column, j];
                }

                vector[row] -= factor * vector[column];
            }
        }

        return Enumerable.Range(0, size)
            .Select(i => matrix[i, i] == 0d ? 0d : vector[i] / matrix[i, i])
            .ToArray();
    }
}

/// <summary>
/// Linear spot price model: intercept, wind index, irradiance, 23 local hour dummies, Saturday and Sunday.
/// </summary>
public sealed class WeatherSpotPriceModel
{
    private readonly double[] coefficients;

    public WeatherSpotPriceModel(double[] coefficients)
    {
        this.coefficients = coefficients;
    }

    public double WindCoefficient => coefficients[1];

    public double SolarCoefficient => coefficients[2];

    public double Predict(PriceDriverHour weather, TimeZoneInfo timeZone)
    {
        var features = CreateFeatures(weather, timeZone);
        return features.Select((feature, index) => feature * coefficients[index]).Sum();
    }

    public static double[] CreateFeatures(PriceDriverHour weather, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(weather.StartsAtUtc, timeZone);
        var features = new double[28];
        features[0] = 1d;
        features[1] = (double)weather.WindIndex;
        features[2] = (double)weather.SolarKwPerSquareMeter;

        if (local.Hour > 0)
        {
            features[2 + local.Hour] = 1d;
        }

        features[26] = local.DayOfWeek == DayOfWeek.Saturday ? 1d : 0d;
        features[27] = local.DayOfWeek == DayOfWeek.Sunday ? 1d : 0d;

        return features;
    }
}
