using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Services;

namespace TibberVictronController.Business.Tests;

public sealed class DynamicTariffComparisonTests
{
    [Fact]
    public void IncludesBasePriceAndRejectsUnconfirmedHourlyOrWrongRegionOffers()
    {
        var at = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var prices = Enumerable.Range(0, 4).Select(i => new EnergyMarketPrice(new(at.AddMinutes(15 * i), at.AddMinutes(15 * (i + 1))), .10m)).ToArray();
        DynamicTariffOffer Offer(string name, decimal surcharge, decimal monthly) => new(name, surcharge, monthly, 19, 15, "Preisblatt", new(2026, 10, 1), "12345");
        var result = DynamicTariffComparisonCalculator.Calculate(new([
            Offer("A", .10m, 20), Offer("B", .12m, 5), Offer("Stundentarif", .01m, 0) with { BillingIntervalMinutes = 60 },
            Offer("Ungeprüft", .01m, 0) with { Source = null }, Offer("Andere Region", .01m, 0) with { PostalCode = "54321" }
        ], prices, 4000, "12345"));
        Assert.Equal("B", result.Offers[0].Name);
        Assert.Equal(.239m, result.Offers[0].GrossWorkPricePerKwh);
        Assert.Equal(1016m, result.Offers[0].EstimatedAnnualCost);
        Assert.Equal(1116m, result.Offers[1].EstimatedAnnualCost);
        Assert.All(result.Offers.Skip(2), o => { Assert.Null(o.EstimatedAnnualCost); Assert.NotNull(o.ExclusionReason); });
    }

    [Fact]
    public void MissingPricesOrIncompleteIntervalsCannotProduceAComparison()
    {
        Assert.Throws<ArgumentException>(() => DynamicTariffComparisonCalculator.Calculate(new([], [], 4000, null)));
        Assert.Throws<ArgumentException>(() => DynamicTariffComparisonCalculator.ParseOffers("not JSON"));
        Assert.Throws<ArgumentException>(() => DynamicTariffComparisonCalculator.ParseOffers("[{\"name\":\"X\",\"vatPercent\":200}]"));
    }
}
