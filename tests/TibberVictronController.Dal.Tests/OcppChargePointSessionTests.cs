using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Ocpp;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;

namespace TibberVictronController.Dal.Tests;

public sealed class OcppChargePointSessionTests : IAsyncLifetime
{
    private const string ChargePointId = "HALO-123";
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly EvChargerStatusStore store = new();
    private readonly Channel<string> sent = Channel.CreateUnbounded<string>();
    private ControllerDbContext db = null!;
    private EfEvChargingSessionRepository repository = null!;
    private OcppChargePointSession session = null!;

    public async Task InitializeAsync()
    {
        connection.Open();
        db = new ControllerDbContext(new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options);
        await new ControllerDbInitializer(db).InitializeAsync(clock.UtcNow);
        repository = new EfEvChargingSessionRepository(db);
        session = CreateSession(store.Connect(ChargePointId, clock.UtcNow));
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        connection.Dispose();
    }

    [Fact]
    public async Task BootNotificationIsAcceptedAndRecordsTheWallbox()
    {
        var reply = await CallAsync("BootNotification", """{"chargePointVendor":"Charge Amps","chargePointModel":"Halo","firmwareVersion":"160"}""");

        Assert.Equal(3, reply[0].GetInt32());
        Assert.Equal("Accepted", reply[2].GetProperty("status").GetString());
        Assert.Equal(60, reply[2].GetProperty("interval").GetInt32());
        Assert.EndsWith("Z", reply[2].GetProperty("currentTime").GetString());
        Assert.Equal("Halo", store.Current.Model);
        Assert.Equal("160", store.Current.FirmwareVersion);
    }

    [Fact]
    public async Task TransactionTracksMeterValuesAndPersistsTheSession()
    {
        await CallAsync("StatusNotification", """{"connectorId":1,"errorCode":"NoError","status":"Charging"}""");
        var start = await CallAsync("StartTransaction", """{"connectorId":1,"idTag":"car","meterStart":1000,"timestamp":"2026-10-31T11:59:00Z"}""");
        var transactionId = start[2].GetProperty("transactionId").GetInt32();
        Assert.Equal("Accepted", start[2].GetProperty("idTagInfo").GetProperty("status").GetString());

        await CallAsync("MeterValues", $$"""
            {"connectorId":1,"transactionId":{{transactionId}},"meterValue":[{"timestamp":"2026-10-31T12:00:00Z","sampledValue":[
              {"value":"3.5","measurand":"Energy.Active.Import.Register","unit":"kWh"},
              {"value":"11000","measurand":"Power.Active.Import","unit":"W"},
              {"value":"15.9","measurand":"Current.Import","phase":"L1","unit":"A"},
              {"value":"80","measurand":"SoC","location":"EV","unit":"Percent"}]}]}
            """);

        Assert.True(store.Current.IsVehiclePlugged);
        Assert.Equal(11000, store.Current.PowerWatts);
        Assert.False(store.Current.PowerIsDerived);
        Assert.Equal(2.5, store.Current.SessionEnergyKwh!.Value, 3);
        Assert.Equal(15.9, store.Current.CurrentL1Amps);

        var stop = await CallAsync("StopTransaction", $$"""{"transactionId":{{transactionId}},"meterStop":5000,"timestamp":"2026-10-31T12:30:00Z","reason":"EVDisconnected"}""");

        Assert.Equal("Accepted", stop[2].GetProperty("idTagInfo").GetProperty("status").GetString());
        Assert.Null(store.Current.ActiveTransactionId);
        Assert.Equal(0, store.Current.PowerWatts);
        var stored = Assert.Single(await repository.GetRecentAsync(5));
        Assert.Equal(4.0, stored.EnergyKwh, 3);
        Assert.Equal("EVDisconnected", stored.StopReason);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task RepeatedStartTransactionKeepsTheTransactionId()
    {
        const string payload = """{"connectorId":1,"idTag":"car","meterStart":1000,"timestamp":"2026-10-31T11:59:00Z"}""";
        var first = await CallAsync("StartTransaction", payload);
        var second = await CallAsync("StartTransaction", payload);

        Assert.Equal(first[2].GetProperty("transactionId").GetInt32(), second[2].GetProperty("transactionId").GetInt32());
        Assert.Single(await repository.GetRecentAsync(5));
    }

    [Fact]
    public async Task NewStartClosesASessionWhoseStopWasLost()
    {
        await CallAsync("StartTransaction", """{"connectorId":1,"idTag":"car","meterStart":1000,"timestamp":"2026-10-31T10:00:00Z"}""");
        await CallAsync("StartTransaction", """{"connectorId":1,"idTag":"car","meterStart":3000,"timestamp":"2026-10-31T11:00:00Z"}""");

        var sessions = await repository.GetRecentAsync(5);
        Assert.Equal(2, sessions.Count);
        Assert.Single(sessions, s => s.IsActive);
        Assert.Equal("ReplacedByNewSession", sessions.Single(s => !s.IsActive).StopReason);
    }

    [Fact]
    public async Task PowerIsDerivedFromTheEnergyRegisterWhenNotReported()
    {
        var start = await CallAsync("StartTransaction", """{"connectorId":1,"idTag":"car","meterStart":0,"timestamp":"2026-10-31T12:00:00Z"}""");
        var id = start[2].GetProperty("transactionId").GetInt32();

        await CallAsync("MeterValues", $$"""{"connectorId":1,"transactionId":{{id}},"meterValue":[{"timestamp":"2026-10-31T12:01:00Z","sampledValue":[{"value":"100"}]}]}""");

        // 100 Wh in one minute = 6 kW.
        Assert.Equal(6000, store.Current.PowerWatts!.Value, 1);
        Assert.True(store.Current.PowerIsDerived);
    }

    [Fact]
    public async Task UnknownActionAndInvalidPayloadAreAnsweredWithCallErrors()
    {
        var unknown = await CallAsync("ReserveNow", "{}");
        var invalid = await CallAsync("StartTransaction", """{"connectorId":1}""");

        Assert.Equal(4, unknown[0].GetInt32());
        Assert.Equal("NotImplemented", unknown[2].GetString());
        Assert.Equal("FormationViolation", invalid[2].GetString());
        Assert.Empty(await repository.GetRecentAsync(5));
    }

    [Fact]
    public async Task MeteringIsConfiguredAfterTheFirstMessageAndSecretsAreHidden()
    {
        var configure = session.ConfigureMeteringAsync(CancellationToken.None);
        await CallAsync("Heartbeat", "{}");

        var getConfiguration = await ReadSentAsync();
        Assert.Equal("GetConfiguration", getConfiguration[2].GetString());
        await AnswerAsync(getConfiguration, """
            {"configurationKey":[
              {"key":"MeterValueSampleInterval","readonly":false,"value":"0"},
              {"key":"MeterValuesSampledData","readonly":false,"value":"Energy.Active.Import.Register"},
              {"key":"AuthorizationKey","readonly":false,"value":"geheim"},
              {"key":"WebSocketPingInterval","readonly":false,"value":"30"}]}
            """);

        var interval = await ReadSentAsync();
        Assert.Equal("MeterValueSampleInterval", interval[3].GetProperty("key").GetString());
        Assert.Equal("10", interval[3].GetProperty("value").GetString());
        await AnswerAsync(interval, """{"status":"Accepted"}""");

        var sampledFull = await ReadSentAsync();
        Assert.Contains("Current.Import", sampledFull[3].GetProperty("value").GetString());
        await AnswerAsync(sampledFull, """{"status":"Rejected"}""");
        var sampledReduced = await ReadSentAsync();
        Assert.Equal("Energy.Active.Import.Register,Power.Active.Import", sampledReduced[3].GetProperty("value").GetString());
        await AnswerAsync(sampledReduced, """{"status":"Accepted"}""");

        await AnswerAsync(await ReadSentAsync(), """{"status":"Accepted"}"""); // TriggerMessage StatusNotification
        await AnswerAsync(await ReadSentAsync(), """{"status":"Accepted"}"""); // TriggerMessage MeterValues
        await configure;

        Assert.False(store.Current.Configuration.ContainsKey("AuthorizationKey"));
        Assert.True(store.Current.Configuration.ContainsKey("WebSocketPingInterval"));
        Assert.Contains("Gesetzt: MeterValueSampleInterval=10", store.Current.ConfigurationNote);
        Assert.Contains("lehnt", store.Current.ConfigurationNote);
    }

    [Fact]
    public void ClosingAnOldConnectionDoesNotOverwriteTheNewOne()
    {
        var oldGeneration = store.Connect(ChargePointId, clock.UtcNow);
        store.Connect(ChargePointId, clock.UtcNow);

        store.Disconnect(oldGeneration);

        Assert.True(store.Current.IsConnected);
    }

    [Fact]
    public async Task OpenSessionIsRestoredAfterAControllerRestart()
    {
        var start = await CallAsync("StartTransaction", """{"connectorId":1,"idTag":"car","meterStart":2000,"timestamp":"2026-10-31T11:00:00Z"}""");
        var id = start[2].GetProperty("transactionId").GetInt32();

        var restartedStore = new EvChargerStatusStore();
        var restarted = new OcppChargePointSession(new OcppConnectionContext(ChargePointId, restartedStore.Connect(ChargePointId, clock.UtcNow),
            (text, _) => sent.Writer.WriteAsync(text).AsTask(), restartedStore, clock, NullLogger.Instance));
        await restarted.RestoreOpenSessionAsync(repository, CancellationToken.None);
        await restarted.HandleTextAsync($$"""[2,"m1","MeterValues",{"connectorId":1,"meterValue":[{"timestamp":"2026-10-31T12:00:00Z","sampledValue":[{"value":"3000"}]}]}]""",
            repository, CancellationToken.None);

        Assert.Equal(id, restartedStore.Current.ActiveTransactionId);
        Assert.Equal(1.0, restartedStore.Current.SessionEnergyKwh!.Value, 3);
    }

    private OcppChargePointSession CreateSession(long generation) => new(new OcppConnectionContext(
        ChargePointId, generation, (text, _) => sent.Writer.WriteAsync(text).AsTask(), store, clock, NullLogger.Instance));

    private int messageCounter;

    private async Task<JsonElement> CallAsync(string action, string payload)
    {
        var id = "c" + Interlocked.Increment(ref messageCounter);
        await session.HandleTextAsync($"[2,\"{id}\",\"{action}\",{payload}]", repository, CancellationToken.None);
        var reply = await ReadSentAsync();
        Assert.Equal(id, reply[1].GetString());
        return reply;
    }

    private async Task<JsonElement> ReadSentAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return JsonDocument.Parse(await sent.Reader.ReadAsync(timeout.Token)).RootElement.Clone();
    }

    private Task AnswerAsync(JsonElement call, string payload) =>
        session.HandleTextAsync($"[3,\"{call[1].GetString()}\",{payload}]", repository, CancellationToken.None);

    private sealed class ManualClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
