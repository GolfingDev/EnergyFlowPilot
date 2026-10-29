using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.JointBattery;

/// <summary>
/// Last successfully loaded planning inputs. When a provider fails, planning continues with these instead of stopping
/// (day-ahead prices do not change; a PV forecast of a few hours ago is still far better than none).
/// Process-local on purpose: after a restart the providers are asked first anyway.
/// </summary>
public sealed class JointBatteryInputCache
{
    private sealed record Entry<T>(IReadOnlyList<T> Items, DateTimeOffset LoadedAtUtc);

    private Entry<TibberPriceForecastSlot>? prices;
    private Entry<PvYieldForecastSlot>? pv;

    public (IReadOnlyList<TibberPriceForecastSlot>? Items, DateTimeOffset? LoadedAtUtc) Prices =>
        Volatile.Read(ref prices) is { } entry ? (entry.Items, entry.LoadedAtUtc) : (null, null);

    public (IReadOnlyList<PvYieldForecastSlot>? Items, DateTimeOffset? LoadedAtUtc) Pv =>
        Volatile.Read(ref pv) is { } entry ? (entry.Items, entry.LoadedAtUtc) : (null, null);

    public void StorePrices(IReadOnlyList<TibberPriceForecastSlot> items, DateTimeOffset loadedAtUtc)
    {
        if (items.Count > 0) Volatile.Write(ref prices, new(items, loadedAtUtc));
    }

    public void StorePv(IReadOnlyList<PvYieldForecastSlot> items, DateTimeOffset loadedAtUtc)
    {
        if (items.Any(p => p.IsForecastAvailable)) Volatile.Write(ref pv, new(items, loadedAtUtc));
    }
}
