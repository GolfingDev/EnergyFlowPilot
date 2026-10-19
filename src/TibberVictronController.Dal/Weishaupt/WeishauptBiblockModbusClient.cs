using System.Buffers.Binary;
using System.Net.Sockets;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.Weishaupt;

/// <summary>
/// Read-only client for the verified WBB input registers. This class deliberately has no FC06/FC16 implementation.
/// </summary>
public sealed class WeishauptBiblockModbusClient : IWeishauptBiblockTelemetrySource
{
    // This WBB expects the full documented numbers on the wire, not offsets from 30001.
    private const ushort OutdoorTemperatureAddress = 30001;
    private const ushort OperatingStatusAddress = 33101;
    private const ushort HotWaterTemperatureAddress = 32102;

    private readonly DatabaseWeishauptBiblockModbusSettingsProvider settingsProvider;

    public WeishauptBiblockModbusClient(DatabaseWeishauptBiblockModbusSettingsProvider settingsProvider)
    {
        this.settingsProvider = settingsProvider;
    }

    public async Task<WeishauptBiblockTelemetrySample> GetCurrentSampleAsync(DateTimeOffset measuredAtUtc, CancellationToken cancellationToken = default)
    {
        if (measuredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Der Messzeitpunkt der Weishaupt-Telemetrie muss in UTC angegeben sein.", nameof(measuredAtUtc));
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (!settings.IsReadyForPolling)
        {
            throw new InvalidOperationException("Weishaupt-Biblock-Modbus ist nicht vollstaendig konfiguriert.");
        }

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(settings.Host, settings.Port, cancellationToken);
        await using var stream = tcpClient.GetStream();

        var outdoorRaw = await ReadInputRegisterAsync(stream, settings.SlaveId, OutdoorTemperatureAddress, 1, cancellationToken);
        var status = await ReadInputRegisterAsync(stream, settings.SlaveId, OperatingStatusAddress, 2, cancellationToken);
        var hotWaterRaw = await ReadInputRegisterAsync(stream, settings.SlaveId, HotWaterTemperatureAddress, 3, cancellationToken);

        return new WeishauptBiblockTelemetrySample
        {
            MeasuredAtUtc = measuredAtUtc,
            OutdoorTemperatureCelsius = (short)outdoorRaw / 10m,
            OperatingStatusCode = status,
            HotWaterTemperatureCelsius = (short)hotWaterRaw / 10m
        };
    }

    private static async Task<ushort> ReadInputRegisterAsync(NetworkStream stream, byte slaveId, ushort address, ushort transactionId, CancellationToken cancellationToken)
    {
        var request = WeishauptBiblockModbusProtocol.CreateReadInputRegisterRequest(transactionId, slaveId, address);
        await stream.WriteAsync(request, cancellationToken);

        var header = new byte[6];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var remainingLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (remainingLength is < 3 or > 254)
        {
            throw new InvalidOperationException("Ungueltige Modbus-TCP-Laenge der Weishaupt Biblock.");
        }

        var response = new byte[6 + remainingLength];
        header.CopyTo(response, 0);
        await stream.ReadExactlyAsync(response.AsMemory(6, remainingLength), cancellationToken);
        return WeishauptBiblockModbusProtocol.ParseSingleInputRegisterResponse(response, transactionId, slaveId);
    }
}
