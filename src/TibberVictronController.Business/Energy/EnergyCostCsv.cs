using System.Globalization;
using System.Text;

namespace TibberVictronController.Business.Energy;

/// <summary>
/// CSV of the evaluated rows of a report for spreadsheets: semicolon separated with decimal comma (German Excel),
/// units in the header, empty cells for unknown values (never 0), plus the data coverage of every row.
/// </summary>
public static class EnergyCostCsv
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Write(EnergyCostReport report, TimeZoneInfo zone)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(';', "Zeitraum von", "Zeitraum bis", "Hausverbrauch (kWh)", "Netzbezug (kWh)", "Einspeisung (kWh)",
            "Bezugskosten (EUR)", "Gewichteter Einkaufspreis (EUR/kWh)", "Grundgebühr (EUR)", "Einspeisevergütung (EUR)",
            "Gesamtkosten nach Vergütung (EUR)", "Abdeckung Netzmessung (%)", "Abdeckung Hausverbrauch (%)", "Netzbezug ohne Preis (kWh)",
            "Bezugskosten vollständig"));
        foreach (var bucket in report.CostBuckets) AppendRow(builder, bucket.StartUtc, bucket.EndUtc, bucket.Totals, zone);
        AppendRow(builder, report.FromUtc, report.EvaluatedUntilUtc, report.Totals, zone, "Summe ");
        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, DateTimeOffset fromUtc, DateTimeOffset toUtc, EnergyTotals totals, TimeZoneInfo zone, string prefix = "")
    {
        string Time(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, zone).ToString("dd.MM.yyyy HH:mm", German);
        static string Number(double? value, string format) => value is { } v ? v.ToString(format, German) : string.Empty;
        static string Money(decimal? value, string format) => value is { } v ? v.ToString(format, German) : string.Empty;
        builder.AppendLine(string.Join(';', prefix + Time(fromUtc), Time(toUtc), Number(totals.HouseConsumptionKwh, "0.000"),
            Number(totals.GridImportKwh, "0.000"), Number(totals.GridExportKwh, "0.000"), Money(totals.ImportCost, "0.00"),
            Money(totals.WeightedPricePerKwh, "0.0000"), Money(totals.BaseFee, "0.00"), Money(totals.FeedInCompensation, "0.00"),
            Money(totals.TotalAfterCompensation, "0.00"), Number(totals.GridCoverage * 100, "0.0"), Number(totals.BalanceCoverage * 100, "0.0"),
            Number(totals.UnpricedImportKwh, "0.000"), totals.ImportCostComplete ? "ja" : "nein (Teilkosten)"));
    }
}
