using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.Tests.TestDoubles;
using TibberVictronController.Dal.Weishaupt;

namespace TibberVictronController.Dal.Tests;

public sealed class WeishauptBiblockModbusClientTests
{
    [Fact]
    public async Task CollectorUsesVerifiedFullRegisterNumbersAndFC04Only()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ushort[] addresses = [30001, 33101, 32102];
        ushort[] values = [50, 19, 445];
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
                var response = new byte[11];
                request.AsSpan(0, 4).CopyTo(response);
                response[5] = 5; response[6] = 1; response[7] = 4; response[8] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(9), values[i]);
                await stream.WriteAsync(response, timeout.Token);
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
        var sample = await new WeishauptBiblockModbusClient(new DatabaseWeishauptBiblockModbusSettingsProvider(store))
            .GetCurrentSampleAsync(now, timeout.Token);
        await server;
        Assert.Equal(5m, sample.OutdoorTemperatureCelsius);
        Assert.Equal((ushort)19, sample.OperatingStatusCode);
        Assert.Equal(44.5m, sample.HotWaterTemperatureCelsius);
    }
}
