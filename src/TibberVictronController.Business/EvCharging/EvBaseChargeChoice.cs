using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.EvCharging;

/// <summary>The base state of charge question of the current charging session, as the plan and the page see it.</summary>
public sealed record EvBaseChargeChoiceState(
    int? TransactionId,
    bool IsBelowBase,
    double? SocPercent,
    double BaseSocPercent,
    EvBaseChargeMode Mode,
    bool IsDecided,
    bool ShouldAsk,
    DateTimeOffset? AskedAtUtc,
    DateTimeOffset? DefaultAtUtc,
    string? DecidedBy);

/// <summary>
/// Operator 2026-11-03: when the car is plugged in below the base state of charge, the drivers are asked whether it
/// should charge at once or by price. Without an answer within 15 minutes it charges by price (base state of charge
/// within a day at the cheapest time). The answer holds for this charging session only.
/// </summary>
public static class EvBaseChargeChoice
{
    public static readonly TimeSpan AnswerTime = TimeSpan.FromMinutes(15);

    public static EvBaseChargeChoiceState Resolve(EvChargingSession? session, double? socPercent, double baseSocPercent)
    {
        if (session is not { IsActive: true })
            return new EvBaseChargeChoiceState(null, false, socPercent, baseSocPercent, EvBaseChargeMode.PriceDriven, false, false, null, null, null);
        var below = socPercent is { } soc && soc < baseSocPercent;
        if (session.BaseChargeMode is { } mode)
            return new EvBaseChargeChoiceState(session.TransactionId, below, socPercent, baseSocPercent, mode, true, false,
                session.BaseChoiceAskedAtUtc, null, session.BaseChoiceBy);
        // Asked once per session; until (and without) an answer the default applies.
        return new EvBaseChargeChoiceState(session.TransactionId, below, socPercent, baseSocPercent, EvBaseChargeMode.PriceDriven, false,
            below && session.BaseChoiceAskedAtUtc is null, session.BaseChoiceAskedAtUtc, session.BaseChoiceAskedAtUtc + AnswerTime, null);
    }
}
