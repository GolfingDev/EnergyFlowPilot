using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Api.Operations;
using TibberVictronController.Api.Push;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Operations;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Tests;

/// <summary>
/// The alert service with fake push, settings and event log. Joint control is "switched on and live" in most tests;
/// the execution state is set the way the real execution sets it.
/// </summary>
public sealed class OperationsAlertBackgroundServiceTests : IDisposable
{
    // 01:00 UTC is 02:00 in Berlin.
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 1, 0, 0, TimeSpan.Zero);
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly Clock clock = new() { UtcNow = Start };
    private readonly Settings settings = new();
    private readonly Events events = new();
    private readonly Sender sender = new();
    private readonly JointBatteryExecutionState execution = new();
    private readonly JointBatteryRuntimeState planning = new();
    private readonly VictronMqttRuntimeStatus mqtt = new();
    private readonly string directory = Directory.CreateTempSubdirectory("efp-alerts-").FullName;
    private readonly ServiceProvider services;
    private long? freeBytes = 20 * Gigabyte;

    public OperationsAlertBackgroundServiceTests()
    {
        services = new ServiceCollection()
            .AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IOperationalEventRepository>(events)
            .AddSingleton<IOperationsAlertSender>(sender)
            .BuildServiceProvider();
        settings.Values[ControllerSettingDefaults.JointBatteryExecutionEnabledKey] = "true";
        settings.Values[ControllerSettingDefaults.VictronDryRunKey] = "false";
    }

    public void Dispose()
    {
        services.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    private OperationsAlertBackgroundService Service() => new(services.GetRequiredService<IServiceScopeFactory>(),
        new OperationsSnapshotReader(execution, planning, mqtt, new OperationsAlertPaths(directory)) { FreeBytes = _ => freeBytes },
        clock, NullLogger<OperationsAlertBackgroundService>.Instance);

    /// <summary>Advances the clock in one-minute evaluations, refreshing the execution state like the real one-second tick.</summary>
    private async Task RunAsync(OperationsAlertBackgroundService service, int minutes, string? executionStatus)
    {
        for (var minute = 0; minute < minutes; minute++)
        {
            clock.UtcNow += TimeSpan.FromMinutes(1);
            SetExecution(executionStatus);
            await service.EvaluateOnceAsync(CancellationToken.None);
        }
    }

    private void SetExecution(string? status)
    {
        if (status is null) return;
        execution.Set(new(status, "Testmeldung mit Geraetedetail 192.168.1.5", clock.UtcNow, status is "Active" or "Restoring"));
        // A controlling execution has a plan in these tests unless a test removes it.
        if (status == "Active" && withPlan)
            planning.Set(new("Shadow", "Testplan", clock.UtcNow, null, new JointBatteryPlan(clock.UtcNow, [],
                [new(new ForecastTimeSlot(clock.UtcNow.AddMinutes(-5), clock.UtcNow.AddMinutes(10)), 0.20m, "EUR", false, null, [], "Testplan", false)], 0, [])));
    }

    private bool withPlan = true;

    [Fact]
    public async Task ABlockedExecutionAlertsOnceAfterTenMinutes()
    {
        var service = Service();
        SetExecution("Blocked");
        await service.EvaluateOnceAsync(CancellationToken.None);

        await RunAsync(service, 9, "Blocked");
        Assert.Empty(sender.Sent);
        Assert.Empty(events.Saved);

        await RunAsync(service, 1, "Blocked");
        var message = Assert.Single(sender.Sent);
        Assert.Equal("operations-alert", message.Type);
        Assert.Equal("Akkusteuerung steht", message.Title);
        Assert.Equal("Die gemeinsame Akkusteuerung ist eingeschaltet, steuert die Akkus aber nicht. " +
            "Status: Gemeinsame Ausführung wartet; Grund im Dashboard. Störung seit 15.11. 02:00 Uhr.", message.Body);
        Assert.Equal("efp-alert-joint-execution-not-controlling", message.Tag);
        Assert.Equal("/", message.Url);
        Assert.Empty(message.Actions);
        var saved = Assert.Single(events.Saved);
        Assert.Equal(("OperationsAlert", "Warning"), (saved.Category, saved.Severity));
        Assert.StartsWith("Betriebswarnung: Akkusteuerung steht.", saved.Message);

        await RunAsync(service, 30, "Blocked");
        Assert.Single(sender.Sent);
        Assert.Single(events.Saved);
    }

    [Fact]
    public async Task AThirtySecondBlockDoesNotAlert()
    {
        var service = Service();
        await RunAsync(service, 1, "Blocked");
        await RunAsync(service, 60, "Active");

        Assert.Empty(sender.Sent);
        Assert.Empty(events.Saved);
    }

    [Fact]
    public async Task ARestoringExecutionAndOneThatFellSilentAlertToo()
    {
        var service = Service();
        await RunAsync(service, 11, "Restoring");
        Assert.Contains("Rücknahme der gemeinsamen Steuerung läuft.", Assert.Single(sender.Sent).Body);

        var silent = Service();
        sender.Sent.Clear();
        SetExecution("Active");
        // The execution stops reporting while it owns the batteries: its state is never refreshed again.
        await RunAsync(silent, 12, null);
        Assert.Contains("meldet sich aber nicht mehr", Assert.Single(sender.Sent).Body);
    }

    [Fact]
    public async Task JointControlSwitchedOffOrInDryRunDoesNotAlert()
    {
        settings.Values[ControllerSettingDefaults.JointBatteryExecutionEnabledKey] = "false";
        var service = Service();
        await RunAsync(service, 20, "Disabled");

        settings.Values[ControllerSettingDefaults.JointBatteryExecutionEnabledKey] = "true";
        settings.Values[ControllerSettingDefaults.VictronDryRunKey] = "true";
        await RunAsync(service, 20, "Shadow");

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task RemindsSixHoursAfterTheAlertAndNotBefore()
    {
        var service = Service();
        await RunAsync(service, 11, "Blocked");
        Assert.Single(sender.Sent);

        await RunAsync(service, 359, "Blocked");
        Assert.Single(sender.Sent);

        await RunAsync(service, 1, "Blocked");
        Assert.Equal(2, sender.Sent.Count);
        Assert.StartsWith("Besteht weiterhin: ", sender.Sent[1].Body);
        Assert.EndsWith("Störung seit 15.11. 02:01 Uhr.", sender.Sent[1].Body);
        // Same tag: the reminder replaces the first notification on the device.
        Assert.Equal(sender.Sent[0].Tag, sender.Sent[1].Tag);
        // A reminder is no new alert in the event log.
        Assert.Single(events.Saved);
    }

    [Fact]
    public async Task ReportsRecoveryOnceTheExecutionControlsAgain()
    {
        var service = Service();
        await RunAsync(service, 11, "Blocked");

        // One good sample is not yet a recovery: a failing execution controls for seconds between two attempts.
        await RunAsync(service, 2, "Active");
        Assert.Single(sender.Sent);

        await RunAsync(service, 1, "Active");
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("Wieder in Ordnung: Akkusteuerung steht", sender.Sent[1].Title);
        Assert.Equal("Akkusteuerung steht: wieder in Ordnung seit 15.11. 02:14 Uhr. Die Störung bestand seit 15.11. 02:01 Uhr.", sender.Sent[1].Body);
        Assert.Equal(sender.Sent[0].Tag, sender.Sent[1].Tag);
        Assert.Equal(["Warning", "Information"], events.Saved.Select(saved => saved.Severity));
        Assert.Equal("Betriebswarnung beendet: Akkusteuerung steht.", events.Saved[1].Message);

        await RunAsync(service, 30, "Active");
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task AFailedPushIsRetriedTheNextMinuteWithoutLosingOrRepeatingTheAlert()
    {
        var service = Service();
        sender.Outcomes.Enqueue((0, 1));
        sender.Outcomes.Enqueue(null); // throws like a push service timeout
        await RunAsync(service, 11, "Blocked");
        Assert.Equal(1, sender.Attempts);
        Assert.Empty(sender.Sent);

        await RunAsync(service, 1, "Blocked");
        Assert.Equal(2, sender.Attempts);
        Assert.Empty(sender.Sent);

        await RunAsync(service, 1, "Blocked");
        Assert.Equal(3, sender.Attempts);
        var message = Assert.Single(sender.Sent);
        // Still the beginning of the problem, not the time of the retry.
        Assert.EndsWith("Störung seit 15.11. 02:01 Uhr.", message.Body);
        Assert.Single(events.Saved);

        await RunAsync(service, 20, "Blocked");
        Assert.Equal(3, sender.Attempts);
    }

    [Fact]
    public async Task WithoutAnySubscribedDeviceTheAlertStaysDueAndIsStoredOnce()
    {
        var service = Service();
        for (var i = 0; i < 5; i++) sender.Outcomes.Enqueue((0, 0));
        await RunAsync(service, 15, "Blocked");

        Assert.Empty(sender.Sent);
        Assert.Single(events.Saved);

        await RunAsync(service, 1, "Blocked");
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task AnEventLogFailureDoesNotStopThePush()
    {
        var service = Service();
        events.Fail = true;
        await RunAsync(service, 11, "Blocked");

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task SwitchedOffNothingIsSentAndTheStateIsCleared()
    {
        var service = Service();
        await RunAsync(service, 11, "Blocked");
        Assert.Single(sender.Sent);

        settings.Values[ControllerSettingDefaults.OperationsAlertsEnabledKey] = "false";
        await RunAsync(service, 5, "Blocked");
        // The problem ends while alerts are off: no recovery message later, nothing remembered.
        await RunAsync(service, 5, "Active");
        settings.Values[ControllerSettingDefaults.OperationsAlertsEnabledKey] = "true";
        await RunAsync(service, 5, "Active");
        Assert.Single(sender.Sent);

        // A new problem needs its full hold time again.
        await RunAsync(service, 10, "Blocked");
        Assert.Single(sender.Sent);
        await RunAsync(service, 1, "Blocked");
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("Akkusteuerung steht", sender.Sent[1].Title);
    }

    [Fact]
    public async Task AlertsAreOnWithoutAStoredSetting()
    {
        Assert.Equal("true", ControllerSettingDefaults.GetDefinitions().Single(definition => definition.Key == ControllerSettingDefaults.OperationsAlertsEnabledKey).DefaultValue);
        Assert.False(settings.Values.ContainsKey(ControllerSettingDefaults.OperationsAlertsEnabledKey));
        var service = Service();
        await RunAsync(service, 11, "Blocked");

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task AControllingExecutionWithoutAPlanAlertsAfterFifteenMinutes()
    {
        var service = Service();
        withPlan = false;
        await RunAsync(service, 15, "Active");
        Assert.Empty(sender.Sent);

        await RunAsync(service, 1, "Active");
        var message = Assert.Single(sender.Sent);
        Assert.Equal("Akkusteuerung ohne Plan", message.Title);
        Assert.Equal("efp-alert-joint-plan-missing", message.Tag);
        Assert.Contains("Notbetrieb Eigenverbrauch", message.Body);
    }

    [Fact]
    public async Task AnOutdatedPlanCountsAsMissing()
    {
        var service = Service();
        SetExecution("Active");
        withPlan = false;
        // The plan set above grows old: the planning no longer delivers.
        await RunAsync(service, 20, "Active");

        Assert.Equal("Akkusteuerung ohne Plan", Assert.Single(sender.Sent).Title);
    }

    [Fact]
    public async Task VictronSilenceAlertsAfterFiveMinutesAndRecoversWithTheNextMessage()
    {
        var service = Service();
        mqtt.MarkStarting();
        mqtt.MarkMessageReceived(clock.UtcNow);
        await RunAsync(service, 4, "Active");
        Assert.Empty(sender.Sent);

        // "Connected" without messages is not enough.
        mqtt.MarkConnected();
        await RunAsync(service, 1, "Active");
        var message = Assert.Single(sender.Sent);
        Assert.Equal("Keine Victron-Messwerte", message.Title);
        Assert.Equal("Vom Victron-System kommen keine MQTT-Nachrichten mehr. Störung seit 15.11. 02:00 Uhr.", message.Body);

        clock.UtcNow += TimeSpan.FromMinutes(1);
        mqtt.MarkMessageReceived(clock.UtcNow);
        SetExecution("Active");
        await service.EvaluateOnceAsync(CancellationToken.None);
        Assert.Equal("Wieder in Ordnung: Keine Victron-Messwerte", sender.Sent[1].Title);
    }

    [Fact]
    public async Task VictronThatNeverSentAnythingAlertsFiveMinutesAfterTheFirstLook()
    {
        var service = Service();
        mqtt.MarkFailed("Testfehler");
        await RunAsync(service, 5, "Active");
        Assert.Empty(sender.Sent);

        await RunAsync(service, 1, "Active");
        Assert.Equal("Keine Victron-Messwerte", Assert.Single(sender.Sent).Title);
    }

    [Fact]
    public async Task LowDiskSpaceAndAnOldBackupAreSeparateAlerts()
    {
        var service = Service();
        freeBytes = Gigabyte;
        File.WriteAllText(Path.Combine(directory, OperationsAlertPaths.BackupMarkerFileName), "2026-11-13T02:10:00Z energyflowpilot-backup-20261113-021000.tar.gpg\n");
        await RunAsync(service, 1, "Active");

        Assert.Equal(["efp-alert-backup-overdue", "efp-alert-database-disk-low"], sender.Sent.Select(message => message.Tag));
        // 13 November 02:10 UTC is 03:10 in Berlin; 36 hours later the backup is overdue.
        Assert.Equal("Die letzte erfolgreiche Datensicherung ist vom 13.11. 03:10 Uhr. Störung seit 14.11. 15:10 Uhr.", sender.Sent[0].Body);
        Assert.Contains("nur noch 1,0 GB frei", sender.Sent[1].Body);
        Assert.Equal(2, events.Saved.Count);
    }

    [Fact]
    public async Task ARecentBackupOrNoBackupMarkerDoesNotAlert()
    {
        var service = Service();
        // No marker: the backup is not installed.
        await RunAsync(service, 2, "Active");
        File.WriteAllText(Path.Combine(directory, OperationsAlertPaths.BackupMarkerFileName), "2026-11-14T02:10:00Z energyflowpilot-backup-20261114-021000.tar.gpg\n");
        await RunAsync(service, 2, "Active");
        // The free space cannot be read: unknown is no alert.
        freeBytes = null;
        await RunAsync(service, 2, "Active");

        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void AnInMemoryDatabaseHasNoDirectoryToWatch()
    {
        Assert.Null(OperationsAlertPaths.FromConnectionString("Data Source=:memory:").DatabaseDirectory);
        Assert.Equal(Path.GetFullPath(directory),
            OperationsAlertPaths.FromConnectionString("Data Source=" + Path.Combine(directory, "controller.db")).DatabaseDirectory);
    }

    [Fact]
    public async Task TheLoopSurvivesATimeoutAndStopsOnShutdown()
    {
        // A database or push timeout is a TaskCanceledException although nobody stops the service. If it left
        // ExecuteAsync, the host would stop the whole process, including the battery control.
        settings.TimeOut = true;
        var service = new OperationsAlertBackgroundService(services.GetRequiredService<IServiceScopeFactory>(),
            new OperationsSnapshotReader(execution, planning, mqtt, new OperationsAlertPaths((string?)null)), clock, NullLogger<OperationsAlertBackgroundService>.Instance)
        { StartupDelay = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(20) };

        await service.StartAsync(CancellationToken.None);
        try
        {
            await settings.ReadsReached(3).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.NotNull(service.ExecuteTask);
            Assert.False(service.ExecuteTask.IsCompleted, "ExecuteAsync ended after a timeout; the host would stop the process.");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(service.ExecuteTask!.IsCompleted);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class Settings : IControllerSettingStore
    {
        private readonly TaskCompletionSource threeReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        private int expectedReads = int.MaxValue;

        public Dictionary<string, string> Values { get; } = [];

        public bool TimeOut { get; set; }

        public Task ReadsReached(int expected)
        {
            Volatile.Write(ref expectedReads, expected);
            if (Volatile.Read(ref reads) >= expected) threeReads.TrySetResult();
            return threeReads.Task;
        }

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ControllerSetting>>([]);

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) >= Volatile.Read(ref expectedReads)) threeReads.TrySetResult();
            if (TimeOut) throw new TaskCanceledException("Zeitüberschreitung im Test.");
            return Task.FromResult(Values.TryGetValue(key, out var value)
                ? new ControllerSetting(key, value, ControllerSettingSensitivity.Normal, Start)
                : null);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Events : IOperationalEventRepository
    {
        public List<OperationalEvent> Saved { get; } = [];

        public bool Fail { get; set; }

        public Task SaveEventAsync(OperationalEvent operationalEvent, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Testfehler.");
            Saved.Add(operationalEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OperationalEvent>> GetRecentEventsAsync(int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OperationalEvent>>(Saved);
    }

    /// <summary>Delivers to one device unless an outcome is queued; a queued null throws like a push service timeout.</summary>
    private sealed class Sender : IOperationsAlertSender
    {
        public Queue<(int Delivered, int Failed)?> Outcomes { get; } = new();

        public List<PushMessage> Sent { get; } = [];

        public int Attempts { get; private set; }

        public Task<(int Delivered, int Failed)> SendAsync(PushMessage message, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Outcomes.TryDequeue(out var outcome))
            {
                if (outcome is null) throw new TaskCanceledException("Zeitüberschreitung im Test.");
                if (outcome.Value.Delivered == 0) return Task.FromResult(outcome.Value);
            }
            Sent.Add(message);
            return Task.FromResult((1, 0));
        }
    }
}
