using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TibberVictronController.Business.Auditing;

/// <summary>
/// Exports Decision Engine audit reports for human review and external comparison tools.
/// </summary>
public static class DecisionAuditExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Exports the audit report as readable JSON.
    /// </summary>
    public static string ExportJson(DecisionAuditReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    /// <summary>
    /// Exports the candidate decision slots as CSV rows for spreadsheet review.
    /// </summary>
    public static string ExportCsv(DecisionAuditReport report)
    {
        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("startsAtUtc;endsAtUtc;action;ruleId;reason;alternativeAction;alternativeRejectedReason;constraintFlags;tibberPricePerKwh;pvKwh;consumptionKwh;gridImportKwh;gridExportKwh;chargedKwh;dischargedKwh;socBeforePercent;expectedSocPercent;targetPowerWatts");

        foreach (var slot in report.DecisionSlots)
        {
            csvBuilder.AppendLine(string.Join(
                ";",
                slot.TimeSlot.StartsAtUtc.ToString("O", CultureInfo.InvariantCulture),
                slot.TimeSlot.EndsAtUtc.ToString("O", CultureInfo.InvariantCulture),
                Escape(slot.Action),
                Escape(slot.RuleId),
                Escape(slot.Reason),
                Escape(slot.AlternativeAction),
                Escape(slot.AlternativeRejectedReason),
                Escape(string.Join(",", slot.ConstraintFlags)),
                FormatDecimal(slot.TibberPricePerKwh),
                FormatDecimal(slot.ExpectedPvYieldKwh),
                FormatDecimal(slot.ExpectedConsumptionKwh),
                FormatDecimal(slot.GridImportKwh),
                FormatDecimal(slot.GridExportKwh),
                FormatDecimal(slot.ChargedEnergyKwh),
                FormatDecimal(slot.DischargedEnergyKwh),
                FormatDecimal(slot.StateOfChargeBeforePercent),
                FormatDecimal(slot.ExpectedSocPercent),
                slot.TargetPowerWatts.ToString(CultureInfo.InvariantCulture)));
        }

        return csvBuilder.ToString();
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        var escapedValue = value.Replace("\"", "\"\"", StringComparison.Ordinal);

        return $"\"{escapedValue}\"";
    }
}
