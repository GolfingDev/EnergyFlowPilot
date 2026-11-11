using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Configuration;

/// <summary>
/// Per-scope switch for <see cref="CachingControllerSettingStore"/>. Off by default, so HTTP requests and other workers
/// keep reading every setting directly. The joint execution turns it on for the scope of one tick.
/// </summary>
public sealed class ScopedSettingReadCache
{
    internal Dictionary<string, ControllerSetting?> Values { get; } = new(StringComparer.Ordinal);

    public bool Enabled { get; private set; }

    public void Enable() => Enabled = true;
}

/// <summary>
/// The joint execution tick runs every second inside the systemd watchdog budget and read dozens of settings one by
/// one from SQLite, several of them twice and the RSCP credentials with a decryption each time. A blocked database
/// (e.g. a backup) then stretched the tick towards the watchdog limit. With the cache enabled, a key is read and
/// decrypted at most once per scope; a save in the same scope drops the cached value. The device switches are never
/// cached: an adapter that re-checks its switch right before writing must see a change made during the tick.
/// </summary>
public sealed class CachingControllerSettingStore(IControllerSettingStore inner, ScopedSettingReadCache cache) : IControllerSettingStore
{
    private static readonly HashSet<string> AlwaysFresh = new(StringComparer.Ordinal)
    {
        ControllerSettingDefaults.JointBatteryExecutionEnabledKey,
        ControllerSettingDefaults.JointBatteryRecoveryPendingKey,
        ControllerSettingDefaults.VictronDryRunKey
    };

    public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
        inner.GetAllSettingsAsync(cancellationToken);

    public async Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!cache.Enabled || AlwaysFresh.Contains(key)) return await inner.GetSettingAsync(key, cancellationToken);
        if (cache.Values.TryGetValue(key, out var cached)) return cached;
        var setting = await inner.GetSettingAsync(key, cancellationToken);
        cache.Values[key] = setting;
        return setting;
    }

    public async Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
    {
        cache.Values.Remove(setting.Key);
        await inner.SaveSettingAsync(setting, cancellationToken);
        // Removed again in case a read slipped in; the next read loads the stored (for credentials: decrypted) value.
        cache.Values.Remove(setting.Key);
    }
}
