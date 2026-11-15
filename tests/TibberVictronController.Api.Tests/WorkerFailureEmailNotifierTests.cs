using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

public sealed class WorkerFailureEmailNotifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task SwitchedOffNotifierDoesNotAskForSmtpSettings(string? enabled)
    {
        var settings = new Settings(enabled);
        var notifier = new WorkerFailureEmailNotifier(settings, new Clock(), NullLogger<WorkerFailureEmailNotifier>.Instance);

        // Host, sender and recipient are not configured: with mails switched off that is no error.
        await notifier.NotifyAsync(new InvalidOperationException("Testfehler"), CancellationToken.None);

        Assert.Equal([ControllerSettingDefaults.WorkerFailureEmailEnabledKey], settings.ReadKeys);
    }

    [Fact]
    public async Task SwitchedOnNotifierStillReportsMissingSmtpSettings()
    {
        var notifier = new WorkerFailureEmailNotifier(new Settings("true"), new Clock(), NullLogger<WorkerFailureEmailNotifier>.Instance);

        await Assert.ThrowsAsync<MissingSettingException>(() =>
            notifier.NotifyAsync(new InvalidOperationException("Testfehler"), CancellationToken.None));
    }

    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }

    private sealed class Settings(string? enabled) : IControllerSettingStore
    {
        public List<string> ReadKeys { get; } = [];

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ControllerSetting>>([]);

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            ReadKeys.Add(key);
            return Task.FromResult(key == ControllerSettingDefaults.WorkerFailureEmailEnabledKey && enabled is not null
                ? new ControllerSetting(key, enabled, ControllerSettingSensitivity.Normal, Now)
                : null);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
