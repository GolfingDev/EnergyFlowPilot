using System.Globalization;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Decisions;

/// <summary>Explains the chosen schedule with its actual prices and energy needs, without restating control rules.</summary>
public static class JointBatteryPlanExplainer
{
    public static JointBatteryPlan Explain(JointBatteryPlan plan, JointBatteryContinuation? continuation)
    {
        string Price(decimal value, string currency) => currency == "EUR"
            ? $"{(value * 100).ToString("0.00", CultureInfo.GetCultureInfo("de-DE"))} ct/kWh"
            : $"{value.ToString("0.000", CultureInfo.GetCultureInfo("de-DE"))} {currency}/kWh";
        var slots = plan.Slots.Select((slot, index) => {
            var watts = slot.Targets.Sum(t => t.SignedPowerWatts);
            var currentPrice = Price(slot.PricePerKwh, slot.Currency);
            var later = plan.Slots.Skip(index + 1).Where(s => s.Targets.Sum(t => t.SignedPowerWatts) < 0).ToArray();
            var laterEnergy = later.Sum(s => -s.Targets.Sum(t => t.SignedPowerWatts));
            var laterPrice = laterEnergy > 0 ? later.Sum(s => s.PricePerKwh * -s.Targets.Sum(t => t.SignedPowerWatts)) / laterEnergy : (decimal?)null;
            var hasEnergy = slot.Targets.Any(t => plan.Batteries.Any(b => b.Id == t.Id && t.SocBeforePercent > b.Configuration.PlanningMinimumStateOfChargePercent + .1m));
            string reason;
            if (watts > 0 && slot.ChargeSource == BatteryChargeSource.PV)
                reason = "Die erwartete PV-Erzeugung übersteigt den Hausbedarf. Der Überschuss wird für später gespeichert.";
            else if (watts > 0)
            {
                reason = $"Netzladung zu {currentPrice}. ";
                if (slot.PricePerKwh < 0) reason += "Der negative Bezugspreis macht das Laden wirtschaftlich attraktiv.";
                else if (laterPrice is { } price && price > slot.PricePerKwh)
                    reason += $"Die Energie wird später bei geplanten Bezugspreisen von durchschnittlich {Price(price, slot.Currency)} genutzt.";
                else reason += "Die Energie wird für späteren Verbrauch bevorratet.";
                if (continuation is { ValuePerDeliveredKwh: > 0, MaximumDeliveredEnergyKwh: > 0 })
                    reason += $" Zusätzlich wird Bedarf nach dem Preiszeitraum erwartet; das historische Preisniveau liegt bei {Price(continuation.HistoricalTypicalPricePerKwh, slot.Currency)}.";
            }
            else if (watts < 0)
                reason = $"Gespeicherte Energie deckt den Hausbedarf und vermeidet Netzbezug zu {currentPrice}.";
            else if (!hasEnergy && slot.ExpectedConsumptionKwh > slot.ExpectedPvKwh)
                reason = $"Die Speicher stehen auf Reserve. Der verbleibende Hausbedarf wird zu {currentPrice} aus dem Netz gedeckt.";
            else if (laterPrice is { } expensive && expensive > slot.PricePerKwh)
                reason = $"Netzbezug kostet jetzt {currentPrice}. Die gespeicherte Energie bleibt für die geplanten teureren Verbrauchszeiten mit durchschnittlich {Price(expensive, slot.Currency)} erhalten.";
            else if (hasEnergy && continuation is { ValuePerDeliveredKwh: > 0, MaximumDeliveredEnergyKwh: > 0 })
                reason = $"Die Energie bleibt für erwarteten späteren Bedarf erhalten. Historisches Preisniveau: {Price(continuation.HistoricalTypicalPricePerKwh, slot.Currency)}; aktueller Preis: {currentPrice}.";
            else if (slot.ExpectedPvKwh >= slot.ExpectedConsumptionKwh)
                reason = "Die erwartete PV-Erzeugung deckt den Hausbedarf. Ein zusätzlicher Akkueinsatz ist derzeit nicht vorgesehen.";
            else reason = $"Bei {currentPrice} sieht der Plan derzeit keinen wirtschaftlich vorteilhaften Akkueinsatz vor.";
            return slot with { Reason = reason };
        }).ToArray();
        return plan with { Slots = slots };
    }
}
