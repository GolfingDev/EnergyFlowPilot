namespace TibberVictronController.Business.Services;

public static class DynamicTariffPrice
{
    /// <summary>The surcharge already includes its own VAT; only the net exchange component is taxed here.</summary>
    public static decimal GrossWorkPrice(decimal netMarketPricePerKwh, decimal vatPercent, decimal grossSurchargePerKwh)
    {
        if (vatPercent is < 0 or > 100 || grossSurchargePerKwh is < 0 or > 5)
            throw new ArgumentException("Mehrwertsteuer oder Bruttoaufschlag liegt ausserhalb des gueltigen Bereichs.");
        return netMarketPricePerKwh * (1m + vatPercent / 100m) + grossSurchargePerKwh;
    }
}
