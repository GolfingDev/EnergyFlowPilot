using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Api.EvCharging;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Api.Tests;

/// <summary>
/// The control cycle with fake adapters. An exception leaving the cycle stops the whole host (.NET default for a
/// failed BackgroundService), and with it the battery control; so nothing but the host stopping may leave it.
/// </summary>
public sealed class EvChargingControlServiceTests : IDisposable
{
    private const int Transaction = 7;
    private readonly Clock clock = new();
    private readonly Settings settings = new();
    private readonly Charger charger = new();
    private readonly Sessions sessions = new();
    private readonly Control control;
    private readonly EvChargingControlState state = new();
    private readonly ServiceProvider services;
    private readonly EvChargingControlService service;

    public EvChargingControlServiceTests()
    {
        control = new Control(clock);
        services = new ServiceCollection()
            .AddSingleton<IControllerSettingStore>(settings)
            .AddSingleton<IEvChargerStatusSource>(charger)
            .AddSingleton<IEvChargingSessionRepository>(sessions)
            .AddSingleton<ICarStatusSource>(new CarSource())
            .AddSingleton<ICurrentSiteTelemetryProvider>(new Telemetry(clock))
            .AddScoped<EvBaseChargeChoiceService>()
            .BuildServiceProvider();
        service = new EvChargingControlService(services.GetRequiredService<IServiceScopeFactory>(), charger, control, state,
            new EvChargeLoadForecast(), clock, NullLogger<EvChargingControlService>.Instance);
        charger.Current = new EvChargerSnapshot { IsConnected = true, ConnectorStatus = EvConnectorStatus.Charging, ActiveTransactionId = Transaction };
        sessions.Current = new EvChargingSession(Transaction, "HALO", 1, "car", clock.UtcNow.AddHours(-1), 1000, null, null, null, null, null);
    }

    public void Dispose() => services.Dispose();

    [Fact]
    public async Task WallboxDroppingTheConnectionDuringACommandDoesNotFaultTheCycle()
    {
        // Stopped by hand: the cycle sends 0 A. The wallbox closes its socket before answering, which cancels the
        // pending call without our token being cancelled.
        sessions.Current = sessions.Current! with { IsStoppedByUser = true, StoppedBy = "Test" };
        control.Next = () => throw new TaskCanceledException();

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(EvChargingMode.Paused, state.Current.Mode);
        Assert.NotNull(state.Current.CommandProblem);

        // The next cycle repeats the command instead of waiting as after a refusal.
        clock.Advance(TimeSpan.FromSeconds(30));
        control.Next = null;
        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal([0d, 0d], control.SentAmps);
        Assert.Null(state.Current.CommandProblem);
    }

    [Fact]
    public async Task SendFailureDoesNotFaultTheCycle()
    {
        sessions.Current = sessions.Current! with { IsStoppedByUser = true };
        control.Next = () => throw new System.Net.WebSockets.WebSocketException("Socket geschlossen");

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Contains("Socket geschlossen", state.Current.CommandProblem);
    }

    [Fact]
    public async Task CancellationThatIsNotTheHostStoppingDoesNotFaultTheCycle()
    {
        // An HTTP timeout anywhere in the cycle arrives as TaskCanceledException.
        settings.Failure = new TaskCanceledException("Zeitüberschreitung");

        await service.RunCycleAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostStoppingEndsTheCycle()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        settings.Failure = new OperationCanceledException(stopping.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunCycleAsync(stopping.Token));
    }

    [Fact]
    public async Task WithoutAPlanTheWallboxGetsNoLimit()
    {
        // No plan service registered: the plan cannot be calculated, and rule 1 leaves the wallbox at full power.
        settings.Values[ControllerSettingDefaults.EvChargingControlEnabledKey] = "true";

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(EvChargingMode.FullPowerFallback, state.Current.Mode);
        Assert.Empty(control.SentAmps);
    }

    [Fact]
    public async Task SwitchingTheAutomaticOffReleasesTheLimit()
    {
        sessions.Current = sessions.Current! with { IsStoppedByUser = true };
        await service.RunCycleAsync(CancellationToken.None);
        sessions.Current = sessions.Current with { IsStoppedByUser = false };
        clock.Advance(TimeSpan.FromSeconds(30));

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(EvChargingMode.Off, state.Current.Mode);
        Assert.Equal(1, control.Clears);
    }

    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class Settings : IControllerSettingStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<ControllerSetting>> GetAllSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ControllerSetting>>([]);

        public Task<ControllerSetting?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            return Task.FromResult(Values.TryGetValue(key, out var value)
                ? new ControllerSetting(key, value, ControllerSettingSensitivity.Normal, DateTimeOffset.UnixEpoch)
                : null);
        }

        public Task SaveSettingAsync(ControllerSetting setting, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Charger : IEvChargerStatusSource
    {
        public EvChargerSnapshot Current { get; set; } = EvChargerSnapshot.Empty;
    }

    private sealed class Control(Clock clock) : IEvChargerControl
    {
        public Func<EvChargerCommandResult>? Next { get; set; }
        public List<double> SentAmps { get; } = [];
        public int Clears { get; private set; }

        public Task<EvChargerCommandResult> LimitCurrentAsync(double amps, TimeSpan validity, CancellationToken cancellationToken = default)
        {
            SentAmps.Add(amps);
            return Task.FromResult(Next?.Invoke() ?? new EvChargerCommandResult(true, "Accepted", "Test", clock.UtcNow, clock.UtcNow + validity));
        }

        public Task<EvChargerCommandResult> ClearLimitAsync(CancellationToken cancellationToken = default)
        {
            Clears++;
            return Task.FromResult(new EvChargerCommandResult(true, "Accepted", "Test", clock.UtcNow, null));
        }
    }

    private sealed class Telemetry(Clock clock) : ICurrentSiteTelemetryProvider
    {
        public Task<CurrentSiteTelemetry> GetCurrentSiteTelemetryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CurrentSiteTelemetry(0, 0, clock.UtcNow, 0));
    }

    private sealed class CarSource : ICarStatusSource
    {
        public CarSnapshot Car => CarSnapshot.Empty;
        public CarDataConnectionState Connection => throw new NotSupportedException();
        public CarStreamDiagnostics Stream => throw new NotSupportedException();
        public CarPollDiagnostics Poll => throw new NotSupportedException();
    }

    private sealed class Sessions : IEvChargingSessionRepository
    {
        public EvChargingSession? Current { get; set; }

        public Task<EvChargingSession?> GetAsync(int transactionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Current?.TransactionId == transactionId ? Current : null);

        public Task<EvChargingSession> StartAsync(EvChargingSessionStart start, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateMeterAsync(int transactionId, double meterWh, DateTimeOffset measuredAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<EvChargingSession?> StopAsync(int transactionId, double meterStopWh, DateTimeOffset stoppedAtUtc, string? reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvChargingSession>> GetRecentAsync(int maxCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkBaseChoiceAskedAsync(int transactionId, DateTimeOffset askedAtUtc, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SetBaseChargeModeAsync(int transactionId, EvBaseChargeMode mode, string decidedBy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetStoppedByUserAsync(int transactionId, bool stopped, string by, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetImmediateTargetAsync(int transactionId, double? targetPercent, string by, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
