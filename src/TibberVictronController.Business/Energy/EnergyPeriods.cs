namespace TibberVictronController.Business.Energy;

public enum EnergyPeriodKind
{
    Day,
    Week,
    Month,
    Quarter,
    Year,
    Custom
}

/// <summary>A period of local calendar days (Europe/Berlin), end exclusive.</summary>
public sealed record EnergyPeriod(EnergyPeriodKind Kind, DateOnly Start, DateOnly EndExclusive)
{
    public int Days => EndExclusive.DayNumber - Start.DayNumber;
}

/// <summary>Calendar periods in local time; the UTC bounds take the daylight saving changes into account.</summary>
public static class EnergyPeriods
{
    public static EnergyPeriod Containing(EnergyPeriodKind kind, DateOnly day) => kind switch
    {
        EnergyPeriodKind.Day => new EnergyPeriod(kind, day, day.AddDays(1)),
        // Weeks start on Monday (ISO 8601).
        EnergyPeriodKind.Week => new EnergyPeriod(kind, day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), day.AddDays(7 - (((int)day.DayOfWeek + 6) % 7))),
        EnergyPeriodKind.Month => new EnergyPeriod(kind, new DateOnly(day.Year, day.Month, 1), new DateOnly(day.Year, day.Month, 1).AddMonths(1)),
        EnergyPeriodKind.Quarter => new EnergyPeriod(kind, new DateOnly(day.Year, (day.Month - 1) / 3 * 3 + 1, 1),
            new DateOnly(day.Year, (day.Month - 1) / 3 * 3 + 1, 1).AddMonths(3)),
        EnergyPeriodKind.Year => new EnergyPeriod(kind, new DateOnly(day.Year, 1, 1), new DateOnly(day.Year + 1, 1, 1)),
        _ => throw new ArgumentException("Ein frei gewählter Zeitraum braucht Start und Ende.", nameof(kind))
    };

    public static EnergyPeriod Custom(DateOnly start, DateOnly endInclusive)
    {
        if (endInclusive < start) throw new ArgumentException("Das Ende des Zeitraums liegt vor dem Anfang.");
        if (endInclusive.DayNumber - start.DayNumber > 3 * 366) throw new ArgumentException("Der Zeitraum darf höchstens drei Jahre umfassen.");
        return new EnergyPeriod(EnergyPeriodKind.Custom, start, endInclusive.AddDays(1));
    }

    /// <summary>The period <paramref name="steps"/> periods earlier (negative) or later; a free period moves by its length.</summary>
    public static EnergyPeriod Shift(EnergyPeriod period, int steps) => period.Kind switch
    {
        EnergyPeriodKind.Day => Containing(period.Kind, period.Start.AddDays(steps)),
        EnergyPeriodKind.Week => Containing(period.Kind, period.Start.AddDays(7 * steps)),
        EnergyPeriodKind.Month => Containing(period.Kind, period.Start.AddMonths(steps)),
        EnergyPeriodKind.Quarter => Containing(period.Kind, period.Start.AddMonths(3 * steps)),
        EnergyPeriodKind.Year => Containing(period.Kind, period.Start.AddYears(steps)),
        _ => period with { Start = period.Start.AddDays(period.Days * steps), EndExclusive = period.EndExclusive.AddDays(period.Days * steps) }
    };

    public static DateTimeOffset StartOfDayUtc(DateOnly day, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    public static DateOnly LocalDay(DateTimeOffset utc, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ToUtc(EnergyPeriod period, TimeZoneInfo zone) =>
        (StartOfDayUtc(period.Start, zone), StartOfDayUtc(period.EndExclusive, zone));

    /// <summary>
    /// The same already elapsed part of an earlier period: same local wall-clock offset from its start (e.g. the 1st to
    /// the 8th 16:00 of the previous month), at most the whole period.
    /// </summary>
    public static DateTimeOffset SameElapsedEnd(EnergyPeriod period, EnergyPeriod earlier, DateTimeOffset evaluatedUntilUtc, TimeZoneInfo zone)
    {
        var (fromUtc, toUtc) = ToUtc(period, zone);
        var (earlierFrom, earlierTo) = ToUtc(earlier, zone);
        if (evaluatedUntilUtc >= toUtc) return earlierTo;
        var localElapsed = TimeZoneInfo.ConvertTime(evaluatedUntilUtc, zone).DateTime - period.Start.ToDateTime(TimeOnly.MinValue);
        var earlierLocalEnd = earlier.Start.ToDateTime(TimeOnly.MinValue).Add(localElapsed);
        var end = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(earlierLocalEnd, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
        return end < earlierFrom ? earlierFrom : end > earlierTo ? earlierTo : end;
    }
}
