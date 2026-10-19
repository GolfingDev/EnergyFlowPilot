using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Persistence;
using TibberVictronController.Dal.Repositories;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Dal.Tests;

public sealed class WeishauptHotWaterControlTests
{
    [Fact]
    public async Task StateSurvivesNewContextAndAdditiveInitialization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options;
        var now = DateTimeOffset.UtcNow;
        var plan = new WeishauptHotWaterPricePlan(now.AddHours(6), now, now.AddHours(1), 0.25m, 0.40m, 0.1125m, 6m, 60, "Preisfenster");
        await using (var db = new ControllerDbContext(options))
        {
            await new ControllerDbInitializer(db).InitializeAsync(now);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"WeishauptHotWaterControlStates\";");
            await new ControllerDbInitializer(db).InitializeAsync(now);
            await new EfWeishauptHotWaterStateStore(db).SaveAsync(new WeishauptHotWaterState
            {
                OriginalCelsius = 45m, RaisedCelsius = 50m, RaisedAtUtc = now, DeviceIdentity = "device",
                LastPlan = plan, Episodes = [new WeishauptHotWaterEpisode
                { StartedAtUtc = now, OriginalCelsius = 45m, RaisedCelsius = 50m, Plan = plan, Trigger = "Preisfenster", WriteConfirmed = true }]
            }, default);
        }
        await using var reloaded = new ControllerDbContext(options);
        var state = await new EfWeishauptHotWaterStateStore(reloaded).LoadAsync(default);
        Assert.Equal(45m, state.OriginalCelsius);
        Assert.Equal(50m, state.RaisedCelsius);
        Assert.Equal(now, state.RaisedAtUtc);
        Assert.Equal("device", state.DeviceIdentity);
        Assert.Equal(0.1125m, state.LastPlan?.EstimatedAdvantagePerKwh);
        Assert.True(Assert.Single(state.Episodes).WriteConfirmed);
    }

    [Fact]
    public async Task TelemetryRangeLoadsOrderedSamplesForMonitoring()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ControllerDbContext>().UseSqlite(connection).Options;
        await using var db = new ControllerDbContext(options);
        var now = DateTimeOffset.UtcNow;
        await new ControllerDbInitializer(db).InitializeAsync(now);
        var repository = new EfWeishauptBiblockTelemetryRepository(db);
        foreach (var offset in new[] { 10, -5, 0 })
            await repository.SaveSampleAsync(new WeishauptBiblockTelemetrySample
            { MeasuredAtUtc = now.AddMinutes(offset), HotWaterTemperatureCelsius = 45m, OutdoorTemperatureCelsius = 10m, OperatingStatusCode = 19 });
        var samples = await repository.GetSamplesAsync(now, now.AddMinutes(10));
        Assert.Single(samples);
        Assert.Equal(now.ToUnixTimeMilliseconds(), samples[0].MeasuredAtUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task PlanningSettingsUseNewDefaultsAndRejectInvalidHour()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(now));
        var provider = new DatabaseWeishauptHotWaterSettingsProvider(store);
        var settings = await provider.GetSettingsAsync(default);
        Assert.False(settings.IsEnabled);
        Assert.Equal(18, settings.DemandHourLocal);
        Assert.Equal(0.03m, settings.MinimumPriceAdvantagePerKwh);
        await store.SaveSettingAsync(new ControllerSetting(ControllerSettingDefaults.WeishauptHotWaterDemandHourKey,
            "25", ControllerSettingSensitivity.Normal, now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetSettingsAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only42103IsWrittenWithFC06AndVerifiedByFC03(bool badEcho)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var frames = new List<byte[]>();
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            for (var index = 0; index < (badEcho ? 2 : 3); index++)
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, timeout.Token);
                frames.Add(request);
                byte[] response;
                if (request[7] == 0x06)
                {
                    response = (byte[])request.Clone();
                    if (badEcho) response[11]++;
                }
                else
                {
                    response = new byte[11];
                    request.AsSpan(0, 4).CopyTo(response);
                    response[5] = 5; response[6] = 1; response[7] = 3; response[8] = 2;
                    BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), index == 0 ? (ushort)450 : (ushort)500);
                }
                // Exercise TCP fragmentation instead of assuming one receive per Modbus frame.
                await stream.WriteAsync(response.AsMemory(0, 4), timeout.Token);
                await stream.WriteAsync(response.AsMemory(4), timeout.Token);
            }
        }, timeout.Token);
        var settings = ControllerSettingDefaults.CreateDefaultSettings(DateTimeOffset.UtcNow)
            .Where(x => x.Key != ControllerSettingDefaults.WeishauptBiblockModbusHostKey && x.Key != ControllerSettingDefaults.WeishauptBiblockModbusPortKey)
            .Append(new ControllerSetting(ControllerSettingDefaults.WeishauptBiblockModbusHostKey, "127.0.0.1", ControllerSettingSensitivity.Normal, DateTimeOffset.UtcNow))
            .Append(new ControllerSetting(ControllerSettingDefaults.WeishauptBiblockModbusPortKey, port.ToString(), ControllerSettingSensitivity.Normal, DateTimeOffset.UtcNow));
        var control = new WeishauptHotWaterModbusControl(new DatabaseWeishauptBiblockModbusSettingsProvider(new FakeControllerSettingStore(settings)));
        var expected = new WeishauptHotWaterReading($"127.0.0.1:{port}/1", 45m);
        if (badEcho)
            await Assert.ThrowsAsync<InvalidOperationException>(() => control.SetNormalTemperatureAsync(expected, 50m, timeout.Token));
        else
            await control.SetNormalTemperatureAsync(expected, 50m, timeout.Token);
        await server;
        Assert.Equal(badEcho ? new byte[] { 3, 6 } : new byte[] { 3, 6, 3 }, frames.Select(f => f[7]));
        Assert.All(frames, frame => Assert.Equal((ushort)2102, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8))));
        Assert.Equal((ushort)500, BinaryPrimitives.ReadUInt16BigEndian(frames[1].AsSpan(10)));
    }
}
