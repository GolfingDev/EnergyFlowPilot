using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests.TestDoubles;

internal sealed class FakeControllerSettingStore : IControllerSettingStore
{
    private readonly Dictionary<string, ControllerSetting> settingsByKey = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<ControllerSetting>>(settingsByKey.Values.ToArray());
    }

    public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        settingsByKey.TryGetValue(key, out var setting);

        return Task.FromResult(setting);
    }

    public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        settingsByKey[setting.Key] = setting;

        return Task.CompletedTask;
    }
}
