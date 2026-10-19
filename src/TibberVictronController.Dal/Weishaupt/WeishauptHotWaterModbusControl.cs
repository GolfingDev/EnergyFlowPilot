using System.Buffers.Binary;
using System.Net.Sockets;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weishaupt;

/// <summary>Only holding register 42103 is addressable; no general-purpose register write API.</summary>
public sealed class WeishauptHotWaterModbusControl(DatabaseWeishauptBiblockModbusSettingsProvider settingsProvider)
    : IWeishauptHotWaterControl
{
    private const ushort NormalSetPointAddress = 2102;

    public async Task<WeishauptHotWaterReading> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var settings = await GetSettingsAsync(timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
        return new WeishauptHotWaterReading(Identity(settings),
            await ReadSetPointAsync(client.GetStream(), settings.SlaveId, 1, timeout.Token));
    }

    public async Task SetNormalTemperatureAsync(WeishauptHotWaterReading expected, decimal targetCelsius, CancellationToken cancellationToken)
    {
        if (targetCelsius is < 35m or > 55m || targetCelsius * 10m != decimal.Truncate(targetCelsius * 10m))
            throw new ArgumentOutOfRangeException(nameof(targetCelsius), "Warmwasser-Sollwert muss zwischen 35 und 55 Grad in 0,1-Grad-Schritten liegen.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var settings = await GetSettingsAsync(timeout.Token);
        if (expected.DeviceIdentity != Identity(settings))
            throw new InvalidOperationException("Weishaupt-Verbindung wurde vor dem Schreiben geaendert.");
        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host, settings.Port, timeout.Token);
        var stream = client.GetStream();
        var current = await ReadSetPointAsync(stream, settings.SlaveId, 1, timeout.Token);
        if (current != expected.NormalSetPointCelsius)
            throw new InvalidOperationException("Weishaupt-Sollwert wurde zwischenzeitlich geaendert; Schreibvorgang abgebrochen.");
        if (current == targetCelsius) return;

        var request = CreateRequest(2, settings.SlaveId, 0x06, (ushort)(targetCelsius * 10));
        var response = await ExchangeAsync(stream, request, timeout.Token);
        if (!response.AsSpan().SequenceEqual(request))
            throw new InvalidOperationException("Weishaupt hat den Warmwasser-Schreibvorgang nicht korrekt bestaetigt.");
        var actual = await ReadSetPointAsync(stream, settings.SlaveId, 3, timeout.Token);
        if (actual != targetCelsius)
            throw new InvalidOperationException("Weishaupt-Warmwassersollwert stimmt beim Ruecklesen nicht mit der Vorgabe ueberein.");
    }

    private async Task<WeishauptBiblockModbusSettings> GetSettingsAsync(CancellationToken ct)
    {
        var settings = await settingsProvider.GetSettingsAsync(ct);
        // Restoration must remain possible after the telemetry/automation switch is disabled.
        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535 || settings.SlaveId is < 1 or > 247)
            throw new InvalidOperationException("Weishaupt-Modbus-Verbindung ist ungueltig.");
        return settings;
    }

    private static string Identity(WeishauptBiblockModbusSettings s) => $"{s.Host.Trim().ToLowerInvariant()}:{s.Port}/{s.SlaveId}";

    private static byte[] CreateRequest(ushort transaction, byte slave, byte function, ushort value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 6);
        bytes[6] = slave;
        bytes[7] = function;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), NormalSetPointAddress);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), value);
        return bytes;
    }

    private static async Task<decimal> ReadSetPointAsync(NetworkStream stream, byte slave, ushort transaction, CancellationToken ct)
    {
        var response = await ExchangeAsync(stream, CreateRequest(transaction, slave, 0x03, 1), ct);
        if (response.Length != 11 || response[8] != 2)
            throw new InvalidOperationException("Ungueltige Weishaupt-Sollwertantwort.");
        var value = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(9)) / 10m;
        if (value is < 35m or > 55m)
            throw new InvalidOperationException("Weishaupt liefert keinen freigegebenen Warmwasser-Normalsollwert.");
        return value;
    }

    private static async Task<byte[]> ExchangeAsync(NetworkStream stream, byte[] request, CancellationToken ct)
    {
        await stream.WriteAsync(request, ct);
        var header = new byte[6];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (length is < 3 or > 254)
            throw new InvalidOperationException("Ungueltige Weishaupt-Modbus-Antwortlaenge.");
        var response = new byte[6 + length];
        header.CopyTo(response, 0);
        await stream.ReadExactlyAsync(response.AsMemory(6), ct);
        if (!response.AsSpan(0, 4).SequenceEqual(request.AsSpan(0, 4)) || response[6] != request[6])
            throw new InvalidOperationException("Weishaupt-Modbus-Antwort gehoert nicht zur Anfrage.");
        if (response[7] == (request[7] | 0x80))
            throw new InvalidOperationException($"Weishaupt-Modbus-Ausnahme: 0x{response[8]:X2}.");
        if (response[7] != request[7])
            throw new InvalidOperationException("Unerwarteter Weishaupt-Modbus-Funktionscode.");
        return response;
    }
}
