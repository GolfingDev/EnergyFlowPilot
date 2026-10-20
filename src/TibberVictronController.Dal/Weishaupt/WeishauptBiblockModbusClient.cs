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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
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
            HotWaterTemperatureCelsius = (short)hotWaterRaw / 10m,
            TotalHeatTodayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36101, 4, cancellationToken),
            TotalHeatYesterdayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36102, 5, cancellationToken),
            TotalHeatMonthKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36103, 6, cancellationToken),
            TotalHeatYearKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36104, 7, cancellationToken),
            HeatingHeatTodayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36201, 8, cancellationToken),
            HeatingHeatYesterdayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36202, 9, cancellationToken),
            HeatingHeatMonthKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36203, 10, cancellationToken),
            HeatingHeatYearKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36204, 11, cancellationToken),
            HotWaterHeatTodayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36301, 12, cancellationToken),
            HotWaterHeatYesterdayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36302, 13, cancellationToken),
            HotWaterHeatMonthKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36303, 14, cancellationToken),
            HotWaterHeatYearKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36304, 15, cancellationToken),
            ElectricalTodayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36701, 16, cancellationToken),
            ElectricalYesterdayKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36702, 17, cancellationToken),
            ElectricalMonthKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36703, 18, cancellationToken),
            ElectricalYearKwh = await ReadOptionalEnergyAsync(stream, settings.SlaveId, 36704, 19, cancellationToken)
        };
    }

    private static async Task<ushort?> ReadOptionalEnergyAsync(NetworkStream stream, byte slaveId, ushort address, ushort transactionId, CancellationToken ct)
    {
        try
        {
            // Energy is unsigned and already in kWh, unlike signed temperatures in tenths.
            return await ReadInputRegisterAsync(stream, slaveId, address, transactionId, ct);
        }
        catch (WeishauptModbusException ex) when (ex.ExceptionCode is 1 or 2)
        {
            // Firmware may not expose optional statistics. Transport/protocol errors still fail polling.
            return null;
        }
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
