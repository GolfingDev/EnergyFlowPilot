using System.Buffers.Binary;

namespace TibberVictronController.Dal.Weishaupt;

/// <summary>Minimal Modbus TCP framing for input-register reads (FC04) only.</summary>
public static class WeishauptBiblockModbusProtocol
{
    public const byte ReadInputRegistersFunctionCode = 0x04;

    public static byte[] CreateReadInputRegisterRequest(ushort transactionId, byte slaveId, ushort address)
    {
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0); // Modbus TCP protocol identifier
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6); // unit id + PDU
        request[6] = slaveId;
        request[7] = ReadInputRegistersFunctionCode;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), 1);
        return request;
    }

    public static ushort ParseSingleInputRegisterResponse(ReadOnlySpan<byte> response, ushort expectedTransactionId, byte expectedSlaveId)
    {
        if (response.Length < 9 || BinaryPrimitives.ReadUInt16BigEndian(response) != expectedTransactionId ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2)) != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4)) != response.Length - 6 || response[6] != expectedSlaveId)
        {
            throw new InvalidOperationException("Ungueltige Modbus-TCP-Antwort der Weishaupt Biblock.");
        }

        if (response[7] == (ReadInputRegistersFunctionCode | 0x80))
        {
            throw new InvalidOperationException($"Weishaupt Biblock hat Modbus FC04 mit Fehlercode 0x{response[8]:X2} beantwortet.");
        }

        if (response.Length != 11 || response[7] != ReadInputRegistersFunctionCode || response[8] != 2)
        {
            throw new InvalidOperationException("Unerwartete Modbus-Funktionsantwort der Weishaupt Biblock.");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(response.Slice(9));
    }
}
