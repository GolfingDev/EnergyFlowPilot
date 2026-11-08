namespace TibberVictronController.Business.Models;

/// <summary>
/// A setting the operation needs is missing or unusable. Carries the setting keys, so the page that shows the message
/// can offer to enter the value right there (operator 2026-11-08), for users with the right to change settings.
/// Derives from <see cref="InvalidOperationException"/>, so existing error handling keeps working unchanged.
/// </summary>
public sealed class MissingSettingException(string message, params string[] settingKeys) : InvalidOperationException(message)
{
    public IReadOnlyList<string> SettingKeys { get; } = settingKeys;

    /// <summary>The setting keys of an exception or its inner exceptions; empty when none refers to settings.</summary>
    public static IReadOnlyList<string> KeysOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is MissingSettingException missing) return missing.SettingKeys;
        return [];
    }
}
