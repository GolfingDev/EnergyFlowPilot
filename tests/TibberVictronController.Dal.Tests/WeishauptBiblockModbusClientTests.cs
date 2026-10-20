using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Dal.Tests;

public sealed class WeishauptBiblockModbusClientTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CollectorUsesVerifiedFullRegisterNumbersAndFC04Only(int energyExceptionCode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ushort[] addresses = [30001, 33101, 32102, 36101, 36102, 36103, 36104, 36201, 36202, 36203, 36204, 36301, 36302, 36303, 36304, 36701, 36702, 36703, 36704];
        ushort[] values = [50, 19, 445, 42, 49, 600, 8000, 38, 37, 391, 5178, 4, 11, 209, 2822, 12, 13, 197, 65535];
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            for (var i = 0; i < addresses.Length; i++)
            {
                var request = new byte[12];
                await stream.ReadExactlyAsync(request, timeout.Token);
                Assert.Equal(4, request[7]);
                Assert.Equal(addresses[i], BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
                Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
                var rejected = i >= 3 && energyExceptionCode != 0;
                var response = new byte[rejected ? 9 : 11];
                request.AsSpan(0, 4).CopyTo(response);
                response[5] = rejected ? (byte)3 : (byte)5;
                response[6] = 1; response[7] = rejected ? (byte)0x84 : (byte)4;
                response[8] = rejected ? (byte)energyExceptionCode : (byte)2;
                if (!rejected) BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), values[i]);
                await stream.WriteAsync(response, timeout.Token);
                if (rejected && energyExceptionCode == 3) break;
            }
        }, timeout.Token);
        var now = DateTimeOffset.UtcNow;
        var store = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(now));
        foreach (var (key, value) in new[]
        {
            (ControllerSettingDefaults.WeishauptBiblockModbusEnabledKey, "true"),
            (ControllerSettingDefaults.WeishauptBiblockModbusHostKey, "127.0.0.1"),
            (ControllerSettingDefaults.WeishauptBiblockModbusPortKey, port.ToString())
        })
            await store.SaveSettingAsync(new ControllerSetting(key, value, ControllerSettingSensitivity.Normal, now));
        var client = new WeishauptBiblockModbusClient(new DatabaseWeishauptBiblockModbusSettingsProvider(store));
        if (energyExceptionCode == 3)
        {
            var error = await Assert.ThrowsAsync<WeishauptModbusException>(() => client.GetCurrentSampleAsync(now, timeout.Token));
            Assert.Equal(3, error.ExceptionCode);
            await server;
            return;
        }
        var sample = await client.GetCurrentSampleAsync(now, timeout.Token);
        await server;
        Assert.Equal(5m, sample.OutdoorTemperatureCelsius);
        Assert.Equal((ushort)19, sample.OperatingStatusCode);
        Assert.Equal(44.5m, sample.HotWaterTemperatureCelsius);
        ushort?[] actual =
        [
            sample.TotalHeatTodayKwh,
            sample.TotalHeatYesterdayKwh,
            sample.TotalHeatMonthKwh,
            sample.TotalHeatYearKwh,
            sample.HeatingHeatTodayKwh,
            sample.HeatingHeatYesterdayKwh,
            sample.HeatingHeatMonthKwh,
            sample.HeatingHeatYearKwh,
            sample.HotWaterHeatTodayKwh,
            sample.HotWaterHeatYesterdayKwh,
            sample.HotWaterHeatMonthKwh,
            sample.HotWaterHeatYearKwh,
            sample.ElectricalTodayKwh,
            sample.ElectricalYesterdayKwh,
            sample.ElectricalMonthKwh,
            sample.ElectricalYearKwh
        ];
        for (var i = 0; i < actual.Length; i++)
            Assert.Equal(energyExceptionCode == 0 ? (ushort?)values[i + 3] : null, actual[i]);
    }
}
