using System.Globalization;
using System.Text;
using TibberVictronController.Api.Authentication;
using TibberVictronController.Business.Energy;

namespace TibberVictronController.Api.Energy;

/// <summary>Page "Stromkosten &amp; Verbrauch": HTTP mapping only, the calculation lives in <see cref="EnergyCostReportService"/>.</summary>
public static class EnergyCostEndpoints
{
    public static IEndpointRouteBuilder MapEnergyCostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/energy-costs/report", GetReportAsync).WithName("GetEnergyCostReport").WithTags("EnergyCosts");
        endpoints.MapGet("/api/energy-costs/report.csv", GetCsvAsync).WithName("GetEnergyCostReportCsv").WithTags("EnergyCosts");
        endpoints.MapGet("/api/energy-costs/day", GetDayAsync).WithName("GetEnergyCostDay").WithTags("EnergyCosts");
        endpoints.MapGet("/api/energy-costs/tariffs", GetTariffsAsync).WithName("GetEnergyTariffPeriods").WithTags("EnergyCosts");
        endpoints.MapPut("/api/energy-costs/tariffs", SaveTariffsAsync).WithName("SaveEnergyTariffPeriods").WithTags("EnergyCosts").RequireAdmin();
        return endpoints;
    }

    private static async Task<IResult> GetReportAsync(string? kind, string? date, string? from, string? to, EnergyCostReportService service,
        CancellationToken cancellationToken)
    {
        if (await PeriodAsync(kind, date, from, to, service, cancellationToken) is not { } period)
            return TypedResults.BadRequest(new { message = "Ungültiger Zeitraum. Erwartet: kind=day|week|month|quarter|year mit date=JJJJ-MM-TT oder kind=custom mit from und to." });
        var report = await service.GetReportAsync(period, cancellationToken);
        var zone = await service.ZoneAsync(cancellationToken);
        return TypedResults.Ok(EnergyCostDtoMapper.Map(report, await service.GetTariffsAsync(cancellationToken), zone));
    }

    private static async Task<IResult> GetCsvAsync(string? kind, string? date, string? from, string? to, EnergyCostReportService service,
        CancellationToken cancellationToken)
    {
        if (await PeriodAsync(kind, date, from, to, service, cancellationToken) is not { } period)
            return TypedResults.BadRequest(new { message = "Ungültiger Zeitraum." });
        var report = await service.GetReportAsync(period, cancellationToken);
        var csv = EnergyCostCsv.Write(report, await service.ZoneAsync(cancellationToken));
        // The byte order mark lets spreadsheet programs read the umlauts correctly.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
        return TypedResults.File(bytes, "text/csv; charset=utf-8",
            $"stromkosten-{period.Start:yyyy-MM-dd}-bis-{period.EndExclusive.AddDays(-1):yyyy-MM-dd}.csv");
    }

    private static async Task<IResult> GetDayAsync(string? date, EnergyCostReportService service, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return TypedResults.BadRequest(new { message = "Ungültiges Datum (JJJJ-MM-TT)." });
        var quarters = await service.GetDayAsync(day, cancellationToken);
        var zone = await service.ZoneAsync(cancellationToken);
        return TypedResults.Ok(new EnergyDayDto(day, quarters.Select(slot => new EnergyQuarterHourDto(
            slot.SlotStartUtc, TimeZoneInfo.ConvertTime(slot.SlotStartUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture),
            Round(slot.GridImportKwh, 4), Round(slot.GridExportKwh, 4), Round(slot.HouseConsumptionKwh, 4), slot.PricePerKwh,
            slot.ImportCost is { } cost ? Math.Round(cost, 4) : null, Math.Round(slot.GridCoveredSeconds / 9.0, 1),
            Math.Round(slot.BalanceCoveredSeconds / 9.0, 1))).ToArray()));
    }

    private static async Task<IResult> GetTariffsAsync(EnergyCostReportService service, CancellationToken cancellationToken) =>
        TypedResults.Ok((await service.GetTariffsAsync(cancellationToken)).Select(EnergyCostDtoMapper.Map).ToArray());

    private static async Task<IResult> SaveTariffsAsync(TariffPeriodDto[]? request, EnergyCostReportService service, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await service.SaveTariffsAsync((request ?? []).Select(dto => new TariffPeriod(dto.ValidFrom, dto.Supplier ?? string.Empty,
                dto.MonthlyBaseFee, dto.FeedInPricePerKwh)), cancellationToken);
            return TypedResults.Ok(saved.Select(EnergyCostDtoMapper.Map).ToArray());
        }
        catch (ArgumentException exception)
        {
            return TypedResults.BadRequest(new { message = exception.Message });
        }
    }

    private static async Task<EnergyPeriod?> PeriodAsync(string? kind, string? date, string? from, string? to, EnergyCostReportService service,
        CancellationToken cancellationToken)
    {
        static DateOnly? Parse(string? value) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
        if (!Enum.TryParse<EnergyPeriodKind>(kind, true, out var periodKind)) return null;
        try
        {
            if (periodKind == EnergyPeriodKind.Custom)
                return Parse(from) is { } start && Parse(to) is { } end ? EnergyPeriods.Custom(start, end) : null;
            return EnergyPeriods.Containing(periodKind, Parse(date) ?? await service.TodayAsync(cancellationToken));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static double? Round(double? value, int digits) => value is { } v ? Math.Round(v, digits) : null;
    private static double Round(double value, int digits) => Math.Round(value, digits);
}
