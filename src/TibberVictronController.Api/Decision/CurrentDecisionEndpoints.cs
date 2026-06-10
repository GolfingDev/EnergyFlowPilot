using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Api.Decision;

/// <summary>
/// Maps direct current-decision endpoints and keeps HTTP concerns outside the Decision Engine.
/// </summary>
public static class CurrentDecisionEndpoints
{
    private const int MaximumDecisionLogCount = 5_000;
    private const int MaximumAggregatedDecisionLogCount = 1_000_000;

    public static IEndpointRouteBuilder MapCurrentDecisionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/decision/current",
            GetCurrentDecisionAsync)
            .WithName("GetCurrentBatteryDecision")
            .WithTags("Decision");

        endpoints.MapGet(
            "/api/decision/logs",
            GetDecisionLogsAsync)
            .WithName("GetCurrentBatteryDecisionLogs")
            .WithTags("Decision");

        endpoints.MapGet(
            "/api/decision/history",
            GetDecisionHistoryAsync)
            .WithName("GetCurrentBatteryDecisionHistory")
            .WithTags("Decision");

        return endpoints;
    }

    public static async Task<IResult> GetCurrentDecisionAsync(
        IDecisionLogRepository decisionLogRepository,
        CancellationToken cancellationToken)
    {
        var logEntries = await decisionLogRepository.GetRecentDecisionsAsync(1, cancellationToken);
        var latestLogEntry = logEntries.FirstOrDefault();

        return latestLogEntry is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(CurrentDecisionDtoMapper.MapCurrent(latestLogEntry));
    }

    public static async Task<IResult> GetDecisionLogsAsync(
        IDecisionLogRepository decisionLogRepository,
        int? maxCount,
        CancellationToken cancellationToken)
    {
        var effectiveMaxCount = Math.Clamp(maxCount ?? 20, 1, MaximumDecisionLogCount);
        var logEntries = await decisionLogRepository.GetRecentDecisionsAsync(effectiveMaxCount, cancellationToken);

        return TypedResults.Ok(logEntries.Select(CurrentDecisionDtoMapper.Map).ToArray());
    }

    public static async Task<IResult> GetDecisionHistoryAsync(
        IDecisionLogRepository decisionLogRepository,
        int? hours,
        int? maxCount,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? aggregateMinutes,
        CancellationToken cancellationToken)
    {
        var effectiveHours = Math.Clamp(hours ?? 24, 1, 24 * 90);
        var effectiveToUtc = NormalizeUtc(toUtc ?? DateTimeOffset.UtcNow, nameof(toUtc));
        var effectiveFromUtc = fromUtc.HasValue
            ? NormalizeUtc(fromUtc.Value, nameof(fromUtc))
            : effectiveToUtc.AddHours(-effectiveHours);

        if (effectiveToUtc <= effectiveFromUtc)
        {
            return TypedResults.BadRequest("Das Ende des Decision-History-Zeitraums muss nach dem Start liegen.");
        }

        if (effectiveToUtc - effectiveFromUtc > TimeSpan.FromDays(90))
        {
            return TypedResults.BadRequest("Der Decision-History-Zeitraum darf maximal 90 Tage umfassen.");
        }

        var effectiveAggregateMinutes = Math.Clamp(aggregateMinutes ?? 0, 0, 24 * 60);
        var effectiveMaxCount = effectiveAggregateMinutes > 0
            ? MaximumAggregatedDecisionLogCount
            : Math.Clamp(maxCount ?? 2_000, 1, MaximumDecisionLogCount);
        var logEntries = await decisionLogRepository.GetDecisionsAsync(
            effectiveFromUtc,
            effectiveToUtc,
            effectiveMaxCount,
            cancellationToken);

        var response = logEntries.Select(CurrentDecisionDtoMapper.Map).ToArray();
        return TypedResults.Ok(effectiveAggregateMinutes > 0
            ? Aggregate(response, TimeSpan.FromMinutes(effectiveAggregateMinutes))
            : response);
    }

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value, string parameterName)
    {
        return value.Offset == TimeSpan.Zero
            ? value
            : value.ToUniversalTime();
    }

    private static DecisionLogEntryResponseDto[] Aggregate(
        IReadOnlyList<DecisionLogEntryResponseDto> entries,
        TimeSpan bucketSize)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<DecisionLogEntryResponseDto>();
        }

        var bucketTicks = bucketSize.Ticks;
        return entries
            .GroupBy(entry => entry.DecidedAtUtc.UtcTicks / bucketTicks * bucketTicks)
            .OrderBy(group => group.Key)
            .Select(group => MapBucket(group.Key, bucketSize, group.OrderBy(entry => entry.DecidedAtUtc).ToArray()))
            .ToArray();
    }

    private static DecisionLogEntryResponseDto MapBucket(
        long bucketStartTicks,
        TimeSpan bucketSize,
        IReadOnlyList<DecisionLogEntryResponseDto> entries)
    {
        var lastEntry = entries[^1];
        var dominantState = entries
            .GroupBy(entry => entry.DecisionState)
            .OrderByDescending(group => group.Count())
            .First()
            .Key;
        var dominantChargeSource = entries
            .Where(entry => entry.DecisionState == dominantState)
            .Select(entry => entry.ChargeSource)
            .LastOrDefault();
        var targetPowerWatts = GetAggregatedTargetPowerWatts(entries, dominantState);
        var signedGridPowerWatts = AverageNullable(entries.Select(GetSignedGridPowerWatts));
        var decidedAtUtc = new DateTimeOffset(bucketStartTicks, TimeSpan.Zero);

        return new DecisionLogEntryResponseDto(
            lastEntry.Id,
            dominantState,
            dominantChargeSource,
            targetPowerWatts,
            decidedAtUtc,
            decidedAtUtc,
            decidedAtUtc.Add(bucketSize),
            AverageNullable(entries.Select(entry => entry.StateOfChargePercent)),
            AverageNullable(entries.Select(entry => entry.TibberPricePerKwh)),
            entries.Select(entry => entry.TibberPriceCurrency).LastOrDefault(currency => !string.IsNullOrWhiteSpace(currency)),
            signedGridPowerWatts is null ? null : Math.Max(0, (int)Math.Round((double)signedGridPowerWatts.Value, MidpointRounding.AwayFromZero)),
            signedGridPowerWatts is null ? null : Math.Max(0, (int)Math.Round((double)-signedGridPowerWatts.Value, MidpointRounding.AwayFromZero)),
            AverageNullable(entries.Select(entry => entry.BatteryPowerWatts)),
            lastEntry.Reasons);
    }

    private static int GetAggregatedTargetPowerWatts(IReadOnlyList<DecisionLogEntryResponseDto> entries, string dominantState)
    {
        if (dominantState == "Idle")
        {
            return 0;
        }

        var matchingEntries = entries
            .Where(entry => entry.DecisionState == dominantState)
            .Select(entry => entry.TargetPowerWatts)
            .ToArray();

        return matchingEntries.Length == 0
            ? 0
            : (int)Math.Round(matchingEntries.Average(), MidpointRounding.AwayFromZero);
    }

    private static int? GetSignedGridPowerWatts(DecisionLogEntryResponseDto entry)
    {
        return entry.GridImportWatts is null && entry.GridExportWatts is null
            ? null
            : (entry.GridImportWatts ?? 0) - (entry.GridExportWatts ?? 0);
    }

    private static decimal? AverageNullable(IEnumerable<decimal?> values)
    {
        var numericValues = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return numericValues.Length == 0 ? null : numericValues.Average();
    }

    private static int? AverageNullable(IEnumerable<int?> values)
    {
        var numericValues = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return numericValues.Length == 0
            ? null
            : (int)Math.Round(numericValues.Average(), MidpointRounding.AwayFromZero);
    }
}
