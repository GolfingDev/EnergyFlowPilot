using System.Net.WebSockets;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.EvCharging;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Business.Tests;

public sealed class EvChargerLimitKeeperTests
{
    private readonly Clock clock = new();
    private readonly FakeControl control;
    private readonly EvChargerLimitKeeper keeper;

    public EvChargerLimitKeeperTests()
    {
        control = new FakeControl(clock);
        keeper = new EvChargerLimitKeeper(control, clock);
    }

    [Fact]
    public async Task UnchangedLimitIsRenewedSevenMinutesBeforeItExpires()
    {
        await keeper.ApplyAsync(6, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(7.5));
        await keeper.ApplyAsync(6, CancellationToken.None);
        Assert.Equal(1, control.Limits);

        clock.Advance(TimeSpan.FromMinutes(1));
        await keeper.ApplyAsync(6, CancellationToken.None);

        Assert.Equal(2, control.Limits);
        Assert.Equal(clock.UtcNow.AddMinutes(15), keeper.AppliedUntilUtc);
    }

    [Fact]
    public async Task ChangedCurrentIsSentAtOnce()
    {
        await keeper.ApplyAsync(6, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await keeper.ApplyAsync(10, CancellationToken.None);

        Assert.Equal([6d, 10d], control.SentAmps);
        Assert.Equal(10, keeper.AppliedAmps);
    }

    [Fact]
    public async Task LostAnswerIsRepeatedWithTheNextCycleAndSeveralTimesBeforeTheLimitExpires()
    {
        await keeper.ApplyAsync(6, CancellationToken.None);
        var expires = keeper.AppliedUntilUtc!.Value;
        clock.Advance(EvChargerLimitKeeper.LimitValidity - EvChargerLimitKeeper.RenewBefore);

        // The wallbox stays silent: each attempt blocks for a minute (30 s per connector), then the 30 s cycle goes on.
        control.Next = _ => { clock.Advance(TimeSpan.FromMinutes(1)); return control.Result(false, EvChargerCommandStatus.NoAnswer); };
        var attemptsBeforeExpiry = 0;
        while (clock.UtcNow < expires)
        {
            var before = control.Limits;
            await keeper.ApplyAsync(6, CancellationToken.None);
            if (control.Limits > before && clock.UtcNow <= expires) attemptsBeforeExpiry++;
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.True(attemptsBeforeExpiry >= 3, $"Nur {attemptsBeforeExpiry} Versuche vor dem Ablauf.");
        Assert.False(keeper.LimitActive);
        Assert.Contains("keine Begrenzung bestätigt", keeper.CommandProblem);
    }

    [Fact]
    public async Task RefusalWaitsFiveMinutesButALostAnswerOnlyOneCycle()
    {
        control.Next = _ => control.Result(false, "Rejected");
        await keeper.ApplyAsync(6, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await keeper.ApplyAsync(6, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(4));
        await keeper.ApplyAsync(6, CancellationToken.None);
        Assert.Equal(1, control.Limits);

        clock.Advance(TimeSpan.FromSeconds(30));
        control.Next = _ => control.Result(false, EvChargerCommandStatus.Disconnected);
        await keeper.ApplyAsync(6, CancellationToken.None);
        Assert.Equal(2, control.Limits);

        clock.Advance(TimeSpan.FromSeconds(30));
        control.Next = null;
        await keeper.ApplyAsync(6, CancellationToken.None);

        Assert.Equal(3, control.Limits);
        Assert.True(keeper.LimitActive);
        Assert.Null(keeper.CommandProblem);
    }

    [Fact]
    public async Task LimitThatRanOutIsNoLongerShownAsActive()
    {
        await keeper.ApplyAsync(0, CancellationToken.None);
        Assert.True(keeper.LimitActive);

        clock.Advance(EvChargerLimitKeeper.LimitValidity);

        Assert.False(keeper.LimitActive);
        Assert.Null(keeper.AppliedAmps);
        // Nothing is left to release.
        await keeper.ApplyAsync(null, CancellationToken.None);
        Assert.Equal(0, control.Clears);
    }

    public static TheoryData<Exception> ConnectionProblems => new()
    {
        new TaskCanceledException(),
        new WebSocketException("Socket geschlossen"),
        new InvalidOperationException("Die Wallbox ist nicht verbunden.")
    };

    [Theory]
    [MemberData(nameof(ConnectionProblems))]
    public async Task ConnectionProblemDuringACommandIsReportedAndRepeatedSoon(Exception problem)
    {
        control.Next = _ => throw problem;

        await keeper.ApplyAsync(6, CancellationToken.None);

        Assert.False(keeper.LimitActive);
        Assert.NotNull(keeper.CommandProblem);
        clock.Advance(TimeSpan.FromSeconds(30));
        control.Next = null;
        await keeper.ApplyAsync(6, CancellationToken.None);
        Assert.True(keeper.LimitActive);
    }

    [Fact]
    public async Task CancellationByTheCallerIsPassedOn()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        control.Next = _ => throw new OperationCanceledException(stopping.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keeper.ApplyAsync(6, stopping.Token));
    }

    [Fact]
    public async Task LostReleaseIsRepeatedButARefusedOneIsLeftToExpire()
    {
        await keeper.ApplyAsync(0, CancellationToken.None);
        control.NextClear = () => throw new TaskCanceledException();
        await keeper.ApplyAsync(null, CancellationToken.None);
        // 0 A may still be in force while full power is wanted: try again.
        Assert.True(keeper.LimitActive);

        clock.Advance(TimeSpan.FromSeconds(30));
        control.NextClear = () => control.Result(false, "Rejected");
        await keeper.ApplyAsync(null, CancellationToken.None);

        Assert.Equal(2, control.Clears);
        Assert.False(keeper.LimitActive);
        Assert.NotNull(keeper.CommandProblem);
    }

    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class FakeControl(Clock clock) : IEvChargerControl
    {
        public Func<double, EvChargerCommandResult>? Next { get; set; }
        public Func<EvChargerCommandResult>? NextClear { get; set; }
        public List<double> SentAmps { get; } = [];
        public int Limits => SentAmps.Count;
        public int Clears { get; private set; }

        public EvChargerCommandResult Result(bool accepted, string status, TimeSpan? validity = null) =>
            new(accepted, status, $"Test: {status}", clock.UtcNow, accepted && validity is { } time ? clock.UtcNow + time : null);

        public Task<EvChargerCommandResult> LimitCurrentAsync(double amps, TimeSpan validity, CancellationToken cancellationToken = default)
        {
            SentAmps.Add(amps);
            return Task.FromResult(Next?.Invoke(amps) ?? Result(true, "Accepted", validity));
        }

        public Task<EvChargerCommandResult> ClearLimitAsync(CancellationToken cancellationToken = default)
        {
            Clears++;
            return Task.FromResult(NextClear?.Invoke() ?? Result(true, "Accepted"));
        }
    }
}
