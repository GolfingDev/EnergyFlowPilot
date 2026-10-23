using Microsoft.AspNetCore.DataProtection;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Configuration;

/// <summary>Encrypts only the new RSCP credentials; other existing settings retain their storage format.</summary>
public sealed class ProtectedRscpSettingStore(IControllerSettingStore inner, IDataProtectionProvider protection) : IControllerSettingStore
{
    private const string Prefix = "efp-rscp:v1:";
    private readonly IDataProtector protector = protection.CreateProtector("EnergyFlowPilot.RscpCredentials.v1");
    private static bool IsCredential(string key) => key is ControllerSettingDefaults.E3dcRscpUserKey or
        ControllerSettingDefaults.E3dcRscpPasswordKey or ControllerSettingDefaults.E3dcRscpKeyKey;

    private ControllerSetting Decode(ControllerSetting setting) => IsCredential(setting.Key) && setting.Value is { } value && value.StartsWith(Prefix, StringComparison.Ordinal)
        ? Copy(setting, protector.Unprotect(value[Prefix.Length..])) : setting;
    private static ControllerSetting Copy(ControllerSetting setting, string value) => new(setting.Key, value, setting.Sensitivity, setting.UpdatedAtUtc);

    public async Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
        (await inner.GetAllSettingsAsync(cancellationToken)).Select(Decode).ToArray();

    public async Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        var setting = await inner.GetSettingAsync(key, cancellationToken);
        return setting is null ? null : Decode(setting);
    }

    public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) =>
        inner.SaveSettingAsync(IsCredential(setting.Key) && setting.IsConfigured
            ? Copy(setting, Prefix + protector.Protect(setting.Value!)) : setting, cancellationToken);
}
