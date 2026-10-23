using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;
using TibberVictronController.Dal.E3dc;
using TibberVictronController.Dal.Tests.TestDoubles;

namespace TibberVictronController.Dal.Tests;

public sealed class RscpProtocolTests
{
    // Generated with the independent python-e3dc/py3rijndael implementation, not this codec.
    private const string First = "/lODKHscKggSDwJaeEpAAz+p4X8Xalk2zQKW1sHdq5rC6YWbbPPB8m7HOGeRdMcY63tqUOOM+CwCiGPxjIS5MA==";
    private const string Second = "dt0VOZRH1JMQlsXzJNB4LiO+RHAnnrjITI54uPXw9RLtIkud+x9oPQ4rkK4E4SaF+yF8qQYSB1Yd121f9F9pAg6CXQAjg1AM2yC+SxeFdnxN506W0CLuTx/Xf4X44T+W";
    private const string Telemetry = "DfjHg0AlGQhxU2UR/oMYwo7EMRwxxIDFkFKJRmzgvUuhfRAmaQI3CeUwNRepqZP5lES3wCFbCWJsqynHNAOfx473Vyui8JfRORqAD7zE8l7YFO6JkGR5SciRwGpaYsH+zt+RxdfBAFs8J3JjFrzGr8ypSB/mrx6RyUHNF/IoohjNXrsWbBUGfqBb9dqX+u12gcIxT352xCCA00I6toeykIR+q/CUqW4I1U5sM250M5MQnOAL3LmsVgSDZWoZzEQpjOWA7ciqYJxIkWq7vHEucF8lnTW4PEoOMtpz23piRuBlEi3IswORcU7Ds+koAFjS5EKN6Sklm6ZD7CQVSuh5MXkRRkMn3prIYW4w1VfG2E9w6QqRhyqx8wxLSgNkbiOjTiywcesoNNzhXyPLX/YLeVZtYmLS/8XMcduql/Sso+ANfD8gK9LeXGLKV3gT3E44qsNnL4i6snDsgMnnhEHxzc1AKYs2aSShUTcG9Q7g+hamN7oj9v7PkzfERrPR7eJ1i4awOEnrDVKkpmt0+WjysF4HBitrH8Mg9yj/RPHR14F/ohB4x1vQsawefjtmkUM5FGq1dpK2u3D5PdBnTSrCHX3gfKtWI7JP1KjRvztHWWcZvKTR8Sdek/2vOVhd+w2rRTXo7yKnD3Er2tQWnVD3p7IwKMX9ZHx8sBqEXUDl91Dl0EFdNXA8YrkdKn/sahLTFsxZQeY0lWD7TL11XbHXZA==";

    [Fact]
    public async Task FragmentedAndCoalescedFramesPreserveCipherStateAndSignedValues()
    {
        using var stream = new FragmentedStream([.. Convert.FromBase64String(First), .. Convert.FromBase64String(Second)]);
        var protocol = new RscpProtocol(stream, "test-only-key");
        var first = Assert.Single(await protocol.ReadFrameAsync(CancellationToken.None));
        Assert.Equal(0x01800002u, first.Tag);
        Assert.Equal(-69m, first.Number());
        var second = Assert.Single(await protocol.ReadFrameAsync(CancellationToken.None));
        Assert.Equal(55.5m, second.Required(0x03800001).Number());
        Assert.Equal(278m, second.Required(0x03800026).Number());
    }

    [Fact]
    public async Task TruncatedCiphertextIsRejectedInsteadOfBecomingZeroPower()
    {
        using var stream = new FragmentedStream(Convert.FromBase64String(First)[..40]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => new RscpProtocol(stream, "test-only-key").ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptCiphertextFailsChecksum()
    {
        var bytes = Convert.FromBase64String(First);
        bytes[^1] ^= 1;
        using var stream = new FragmentedStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RscpProtocol(stream, "test-only-key").ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsReadingIncompleteFrame()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new FragmentedStream(Convert.FromBase64String(First));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RscpProtocol(stream, "test-only-key").ReadFrameAsync(cts.Token));
    }

    [Fact]
    public async Task AdapterAuthenticatesAndOnlySendsExpectedReadRequests()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        uint[] requests = [1, 0x03040000, 0x1B000009, 0x01000234, 0x0100008B, 0x01000002, 0x01000041, 0x01000045, 0x01000011];
        var server = Task.Run(async () => {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            var protocol = new RscpProtocol(stream, "test-only-key");
            // Deliberately coalesce responses in one write; the reader must retain the next encrypted frame.
            await stream.WriteAsync(Convert.FromBase64String(Telemetry), timeout.Token);
            foreach (var tag in requests)
            {
                var request = Assert.Single(await protocol.ReadFrameAsync(timeout.Token));
                Assert.Equal(tag, request.Tag);
                Assert.NotEqual(0x01000030u, request.Tag);
            }
        }, timeout.Token);
        var clock = new Clock();
        var settings = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(clock.UtcNow));
        foreach (var (key, value) in new[] {
            (ControllerSettingDefaults.E3dcRscpHostKey, "127.0.0.1"), (ControllerSettingDefaults.E3dcRscpPortKey, port.ToString()),
            (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"), (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"),
            (ControllerSettingDefaults.E3dcRscpKeyKey, "test-only-key") })
            await settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Sensitive, clock.UtcNow));
        var result = await new E3dcRscpTelemetrySource(settings, clock).ReadAsync(timeout.Token);
        await server;
        Assert.Equal(-69, result.BatteryPowerWatts);
        Assert.Equal(97, result.EffectiveChargeLimitWatts);
        Assert.Equal(3000, result.MaximumChargeWatts);
        Assert.InRange(result.UsableCapacityKwh, 14.01m, 14.02m);
        Assert.Equal(1.513m, result.ReserveKwh);
        Assert.True(result.WeatherRegulationEnabled);
    }

    [Theory]
    [InlineData(E3dcPowerMode.ChargeGrid, 4, 2)]
    [InlineData(E3dcPowerMode.Discharge, 2, 1)]
    [InlineData(E3dcPowerMode.ChargePv, 3, 2)]
    public async Task PowerAdapterWritesOnlyEphemeralModeAndPositiveInt32Power(E3dcPowerMode mode, int wireMode, int readMode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () => {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            var protocol = new RscpProtocol(stream, "test-only-key");
            var encrypt = new CbcBlockCipher(new RijndaelEngine(256));
            var key = Enumerable.Repeat((byte)255, 32).ToArray();
            Encoding.UTF8.GetBytes("test-only-key").CopyTo(key, 0);
            encrypt.Init(true, new ParametersWithIV(new KeyParameter(key), Enumerable.Repeat((byte)255, 32).ToArray()));
            async Task Reply(RscpValue value)
            {
                var frame = RscpProtocol.Frame(RscpProtocol.EncodeValue(value));
                var padded = new byte[(frame.Length + 31) / 32 * 32]; frame.CopyTo(padded, 0);
                var cipher = new byte[padded.Length];
                for (var i = 0; i < padded.Length; i += 32) encrypt.ProcessBlock(padded, i, cipher, i);
                await stream.WriteAsync(cipher, timeout.Token);
            }
            Assert.Equal(1u, Assert.Single(await protocol.ReadFrameAsync(timeout.Token)).Tag);
            await Reply(new(0x00800001, 3, [10]));
            var command = Assert.Single(await protocol.ReadFrameAsync(timeout.Token));
            Assert.Equal(0x01000030u, command.Tag);
            Assert.Equal(2, command.Children().Count);
            Assert.Equal(3, command.Required(0x01000031).Type);
            Assert.Equal(wireMode, command.Required(0x01000031).Number());
            Assert.Equal(6, command.Required(0x01000032).Type);
            Assert.Equal(500, command.Required(0x01000032).Number());
            await Reply(new(0x01800030, 7, BitConverter.GetBytes(500u)));
            Assert.Equal(0x01000011u, Assert.Single(await protocol.ReadFrameAsync(timeout.Token)).Tag);
            await Reply(new(0x01800011, 3, [(byte)readMode]));
            Assert.Equal(0x01000002u, Assert.Single(await protocol.ReadFrameAsync(timeout.Token)).Tag);
            await Reply(new(0x01800002, 6, BitConverter.GetBytes(mode == E3dcPowerMode.Discharge ? -495 : 495)));
        }, timeout.Token);
        var clock = new Clock();
        var settings = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(clock.UtcNow));
        foreach (var (key, value) in new[] {
            (ControllerSettingDefaults.E3dcRscpHostKey, "127.0.0.1"), (ControllerSettingDefaults.E3dcRscpPortKey, port.ToString()),
            (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"), (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"),
            (ControllerSettingDefaults.E3dcRscpKeyKey, "test-only-key"), (ControllerSettingDefaults.JointBatteryExecutionEnabledKey, "true") })
            await settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Sensitive, clock.UtcNow));
        var result = await new E3dcRscpPowerControl(settings, clock).ApplyAsync(new(mode, 500), timeout.Token);
        await server;
        Assert.Equal(500, result.EchoPowerWatts);
        Assert.Equal(readMode, result.OperatingMode);
        Assert.Equal(mode == E3dcPowerMode.Discharge ? -495 : 495, result.MeasuredPowerWatts);
    }

    [Fact]
    public async Task SharedSessionReusesOneLoginAndReconnectsAfterDeviceClosesIt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logins = 0;
        var server = Task.Run(async () => {
            // First session: one login serves two commands, then the device drops the connection.
            using (var peer = await listener.AcceptTcpClientAsync(timeout.Token))
            {
                var device = new FakeDevice(peer.GetStream());
                await device.ExpectLoginAsync(timeout.Token); logins++;
                await device.ServeIdleCommandAsync(timeout.Token);
                await device.ServeIdleCommandAsync(timeout.Token);
            }
            using (var peer = await listener.AcceptTcpClientAsync(timeout.Token))
            {
                var device = new FakeDevice(peer.GetStream());
                await device.ExpectLoginAsync(timeout.Token); logins++;
                await device.ServeIdleCommandAsync(timeout.Token);
            }
        }, timeout.Token);
        var clock = new Clock();
        var settings = new FakeControllerSettingStore(ControllerSettingDefaults.CreateDefaultSettings(clock.UtcNow));
        foreach (var (key, value) in new[] {
            (ControllerSettingDefaults.E3dcRscpHostKey, "127.0.0.1"), (ControllerSettingDefaults.E3dcRscpPortKey, port.ToString()),
            (ControllerSettingDefaults.E3dcRscpUserKey, "test-user"), (ControllerSettingDefaults.E3dcRscpPasswordKey, "test-password"),
            (ControllerSettingDefaults.E3dcRscpKeyKey, "test-only-key") })
            await settings.SaveSettingAsync(new(key, value, ControllerSettingSensitivity.Sensitive, clock.UtcNow));
        using var session = new E3dcRscpSession();
        var control = new E3dcRscpPowerControl(settings, clock, session);
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, (await control.ApplyAsync(new(E3dcPowerMode.Idle, 0), timeout.Token)).EchoPowerWatts);
        await server;
        Assert.Equal(2, logins);
    }

    /// <summary>Device side of one RSCP connection with its own sending cipher chain.</summary>
    private sealed class FakeDevice
    {
        private readonly Stream stream;
        private readonly RscpProtocol reader;
        private readonly CbcBlockCipher encrypt = new(new RijndaelEngine(256));

        internal FakeDevice(Stream stream)
        {
            this.stream = stream;
            reader = new RscpProtocol(stream, "test-only-key");
            var key = Enumerable.Repeat((byte)255, 32).ToArray();
            Encoding.UTF8.GetBytes("test-only-key").CopyTo(key, 0);
            encrypt.Init(true, new ParametersWithIV(new KeyParameter(key), Enumerable.Repeat((byte)255, 32).ToArray()));
        }

        private async Task<uint> ReceiveAsync(CancellationToken token) => Assert.Single(await reader.ReadFrameAsync(token)).Tag;

        private async Task ReplyAsync(RscpValue value, CancellationToken token)
        {
            var frame = RscpProtocol.Frame(RscpProtocol.EncodeValue(value));
            var padded = new byte[(frame.Length + 31) / 32 * 32]; frame.CopyTo(padded, 0);
            var cipher = new byte[padded.Length];
            for (var i = 0; i < padded.Length; i += 32) encrypt.ProcessBlock(padded, i, cipher, i);
            await stream.WriteAsync(cipher, token);
        }

        internal async Task ExpectLoginAsync(CancellationToken token)
        {
            Assert.Equal(1u, await ReceiveAsync(token));
            await ReplyAsync(new(0x00800001, 3, [10]), token);
        }

        internal async Task ServeIdleCommandAsync(CancellationToken token)
        {
            Assert.Equal(0x01000030u, await ReceiveAsync(token));
            await ReplyAsync(new(0x01800030, 7, BitConverter.GetBytes(0u)), token);
            Assert.Equal(0x01000011u, await ReceiveAsync(token));
            await ReplyAsync(new(0x01800011, 3, [0]), token);
            Assert.Equal(0x01000002u, await ReceiveAsync(token));
            await ReplyAsync(new(0x01800002, 6, BitConverter.GetBytes(0)), token);
        }
    }

    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero); }
}
