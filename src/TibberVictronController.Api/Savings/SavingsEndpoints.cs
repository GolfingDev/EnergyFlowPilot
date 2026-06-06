using Microsoft.AspNetCore.Mvc;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Savings;

/// <summary>
/// Maps savings reporting endpoints without placing accounting logic in HTTP handlers.
/// </summary>
public static class SavingsEndpoints
{
    private static readonly HashSet<string> SupportedPeriods = new(StringComparer.OrdinalIgnoreCase)
    {
        "day",
        "week",
        "month",
        "year",
        "total"
    };

    /// <summary>
    /// Registers battery savings reporting endpoints used by the Vue frontend.
    /// </summary>
    public static IEndpointRouteBuilder MapSavingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/savings",
            GetSavingsAsync)
            .WithName("GetBatterySavings")
            .WithTags("Savings");

        return endpoints;
    }

    /// <summary>
    /// Returns aggregated savings and daily source rows for the requested reporting period.
    /// </summary>
    public static async Task<IResult> GetSavingsAsync(
        string period,
        DateOnly? referenceDate,
        string? currency,
        [FromServices]
        IBatterySavingsRepository batterySavingsRepository,
        [FromServices]
        IBatterySavingsAccountingService batterySavingsAccountingService,
        CancellationToken cancellationToken)
    {
        if (!SupportedPeriods.Contains(period))
        {
            return TypedResults.BadRequest(new BatterySavingsErrorDto
            {
                Message = "Der Ersparnis-Zeitraum muss day, week, month, year oder total sein."
            });
        }

        if (!string.Equals(period, "total", StringComparison.OrdinalIgnoreCase) && referenceDate is null)
        {
            return TypedResults.BadRequest(new BatterySavingsErrorDto
            {
                Message = "Fuer diesen Ersparnis-Zeitraum muss ein Referenzdatum angegeben werden."
            });
        }

        var query = CreateQuery(new BatterySavingsQueryRequest
        {
            Period = period,
            ReferenceDate = referenceDate,
            Currency = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency
        });

        if (!string.Equals(period, "total", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await batterySavingsAccountingService.RefreshAsync(query, cancellationToken);
            }
            catch
            {
                // Reporting must still return the latest persisted summaries when live accounting cannot refresh.
            }
        }

        var dailySummaries = await batterySavingsRepository.GetDailySummariesAsync(query, cancellationToken);
        var aggregate = BatterySavingsAggregate.FromDailySummaries(dailySummaries);
        var response = BatterySavingsDtoMapper.Map(new BatterySavingsDtoMappingInput
        {
            Period = period.ToLowerInvariant(),
            ReferenceDate = referenceDate,
            Query = query,
            DailySummaries = dailySummaries,
            Aggregate = aggregate
        });

        return TypedResults.Ok(response);
    }

    private static BatterySavingsQuery CreateQuery(BatterySavingsQueryRequest request)
    {
        var dateRange = request.Period.ToLowerInvariant() switch
        {
            "day" => CreateDayRange(request.ReferenceDate!.Value),
            "week" => CreateWeekRange(request.ReferenceDate!.Value),
            "month" => CreateMonthRange(request.ReferenceDate!.Value),
            "year" => CreateYearRange(request.ReferenceDate!.Value),
            _ => new BatterySavingsDateRange
            {
                StartDate = DateOnly.MinValue,
                EndDate = DateOnly.MaxValue
            }
        };

        return new BatterySavingsQuery
        {
            StartDate = dateRange.StartDate,
            EndDate = dateRange.EndDate,
            Currency = request.Currency
        };
    }

    private static BatterySavingsDateRange CreateDayRange(DateOnly referenceDate)
    {
        return new BatterySavingsDateRange
        {
            StartDate = referenceDate,
            EndDate = referenceDate
        };
    }

    private static BatterySavingsDateRange CreateWeekRange(DateOnly referenceDate)
    {
        var daysSinceMonday = ((int)referenceDate.DayOfWeek + 6) % 7;
        var weekStartsAt = referenceDate.AddDays(-daysSinceMonday);

        return new BatterySavingsDateRange
        {
            StartDate = weekStartsAt,
            EndDate = weekStartsAt.AddDays(6)
        };
    }

    private static BatterySavingsDateRange CreateMonthRange(DateOnly referenceDate)
    {
        var monthStartsAt = new DateOnly(referenceDate.Year, referenceDate.Month, 1);

        return new BatterySavingsDateRange
        {
            StartDate = monthStartsAt,
            EndDate = monthStartsAt.AddMonths(1).AddDays(-1)
        };
    }

    private static BatterySavingsDateRange CreateYearRange(DateOnly referenceDate)
    {
        return new BatterySavingsDateRange
        {
            StartDate = new DateOnly(referenceDate.Year, 1, 1),
            EndDate = new DateOnly(referenceDate.Year, 12, 31)
        };
    }

    private sealed class BatterySavingsQueryRequest
    {
        public string Period { get; init; } = string.Empty;

        public DateOnly? ReferenceDate { get; init; }

        public string Currency { get; init; } = "EUR";
    }

    private sealed class BatterySavingsDateRange
    {
        public DateOnly StartDate { get; init; }

        public DateOnly EndDate { get; init; }
    }
}
