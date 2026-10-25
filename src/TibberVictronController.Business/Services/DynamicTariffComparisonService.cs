using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Services;

public sealed record TariffComparisonRequest(decimal AnnualConsumptionKwh, string? PostalCode, int? Days);

public sealed class DynamicTariffComparisonService(IControllerSettingStore settings, IEnergyMarketPriceProvider market, IUtcClock clock)
{
    public async Task<DynamicTariffComparisonResult> CompareAsync(TariffComparisonRequest request, CancellationToken token = default)
    {
        var (annualConsumptionKwh, postalCode, days) = request;
            if (annualConsumptionKwh is <= 0 or > 1000000 || days is < 1 or > 30 ||
                !string.IsNullOrWhiteSpace(postalCode) && (postalCode.Length != 5 || postalCode.Any(c => !char.IsAsciiDigit(c))))
                throw new ArgumentException("Bitte gueltigen Jahresverbrauch, 1 bis 30 Vergleichstage und gegebenenfalls eine fuenfstellige PLZ angeben.");
            var zone = (await settings.GetSettingAsync(ControllerSettingDefaults.EnergyChartsZoneKey, token))?.Value ?? "DE-LU";
            var offers = DynamicTariffComparisonCalculator.ParseOffers((await settings.GetSettingAsync(ControllerSettingDefaults.TariffComparisonOffersKey, token))?.Value);
            var today = TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).Date;
            if (offers.Any(o => o.VerifiedAt > DateOnly.FromDateTime(today)))
                throw new ArgumentException("Der Preisstand eines Angebots darf nicht in der Zukunft liegen.");
            DateTimeOffset Midnight(DateTime d) => new(TimeZoneInfo.ConvertTimeToUtc(d, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")), TimeSpan.Zero);
            var start = Midnight(today.AddDays(-(days ?? 14))); var end = Midnight(today);
            var prices = await market.GetMarketPricesAsync(new(start, end, zone), token);
            if (prices.Count == 0 || prices[0].TimeSlot.StartsAtUtc != start || prices[^1].TimeSlot.EndsAtUtc != end)
                throw new InvalidOperationException("Energy-Charts deckt den Vergleichszeitraum nicht vollstaendig ab.");
            return DynamicTariffComparisonCalculator.Calculate(new(offers, prices, annualConsumptionKwh, postalCode));
    }
}
