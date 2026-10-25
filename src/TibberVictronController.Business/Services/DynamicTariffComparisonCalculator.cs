using System.Text.Json;
using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Business.Services;

public sealed record DynamicTariffOffer(string Name, decimal? GrossSurchargePerKwh, decimal? MonthlyBasePrice,
    decimal VatPercent, int BillingIntervalMinutes, string? Source, DateOnly? VerifiedAt, string? PostalCode);
public sealed record DynamicTariffComparisonInput(IReadOnlyList<DynamicTariffOffer> Offers, IReadOnlyList<EnergyMarketPrice> Prices,
    decimal AnnualConsumptionKwh, string? PostalCode);
public sealed record DynamicTariffComparisonRow(string Name, decimal? GrossWorkPricePerKwh, decimal? EstimatedAnnualCost,
    string? ExclusionReason, string? Source, DateOnly? VerifiedAt);
public sealed record DynamicTariffComparisonResult(DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, int PriceSlots,
    decimal AverageNetMarketPricePerKwh, decimal AnnualConsumptionKwh, string? PostalCode, IReadOnlyList<DynamicTariffComparisonRow> Offers);

public static class DynamicTariffComparisonCalculator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<DynamicTariffOffer> ParseOffers(string? json)
    {
        try
        {
            if (json?.Length > 100000) throw new ArgumentException("Die Tarifliste ist zu gross.");
            var offers = JsonSerializer.Deserialize<DynamicTariffOffer[]>(json ?? "[]", JsonOptions)
                ?? throw new ArgumentException("Die Tarifliste muss ein JSON-Array sein.");
            if (offers.Length > 50 || offers.Any(o => o is null || string.IsNullOrWhiteSpace(o.Name) || o.Name.Length > 120 ||
                o.GrossSurchargePerKwh is < 0 or > 5 || o.MonthlyBasePrice is < 0 or > 1000 || o.VatPercent is < 0 or > 100 ||
                o.BillingIntervalMinutes is not (0 or 15 or 30 or 60) || o.Source?.Length > 1000 ||
                o.PostalCode is { Length: > 0 } code && (code.Length != 5 || code.Any(c => !char.IsAsciiDigit(c)))))
                throw new ArgumentException("Tarifangebote brauchen gueltige Namen, Preise und Abrechnungsintervalle; maximal 50 Angebote.");
            return offers;
        }
        catch (JsonException error) { throw new ArgumentException("Die Tarifangebote konnten nicht gelesen werden.", error); }
    }

    public static DynamicTariffComparisonResult Calculate(DynamicTariffComparisonInput input)
    {
        if (input.AnnualConsumptionKwh is <= 0 or > 1000000 || input.Prices.Count == 0)
            throw new ArgumentException("Jahresverbrauch und veroeffentlichte Boersenpreise werden fuer den Vergleich benoetigt.");
        var prices = input.Prices.OrderBy(p => p.TimeSlot.StartsAtUtc).ToArray();
        if (prices.Any(p => !p.TimeSlot.IsFifteenMinuteSlot) || prices.Zip(prices.Skip(1)).Any(p => p.First.TimeSlot.EndsAtUtc != p.Second.TimeSlot.StartsAtUtc))
            throw new InvalidOperationException("Der Tarifvergleich benoetigt eine lueckenlose Viertelstunden-Preisreihe.");
        var average = prices.Average(p => p.NetPricePerKwh);
        var rows = input.Offers.Select(offer => {
            var exclusion = offer.BillingIntervalMinutes != 15 ? "15-Minuten-Abrechnung nicht bestaetigt."
                : offer.GrossSurchargePerKwh is null || offer.MonthlyBasePrice is null ? "Aufschlag oder Grundpreis fehlt."
                : string.IsNullOrWhiteSpace(offer.Source) || offer.VerifiedAt is null ? "Quelle oder Preisstand fehlt."
                : !string.IsNullOrWhiteSpace(offer.PostalCode) && offer.PostalCode != input.PostalCode ? "Angebot gilt fuer eine andere oder noch nicht angegebene Postleitzahl."
                : null;
            decimal? work = exclusion is null ? DynamicTariffPrice.GrossWorkPrice(average, offer.VatPercent, offer.GrossSurchargePerKwh!.Value) : null;
            return new DynamicTariffComparisonRow(offer.Name, work,
                work * input.AnnualConsumptionKwh + offer.MonthlyBasePrice * 12m, exclusion, offer.Source, offer.VerifiedAt);
        }).OrderBy(row => row.EstimatedAnnualCost is null).ThenBy(row => row.EstimatedAnnualCost).ToArray();
        return new(prices[0].TimeSlot.StartsAtUtc, prices[^1].TimeSlot.EndsAtUtc, prices.Length, average,
            input.AnnualConsumptionKwh, input.PostalCode, rows);
    }
}
