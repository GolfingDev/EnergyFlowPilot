using System.Buffers.Binary;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace TibberVictronController.Dal.E3dc;

internal sealed record RscpValue(uint Tag, byte Type, byte[] Data)
{
    internal static RscpValue Request(uint tag) => new(tag, 0, []);
    internal static RscpValue Container(uint tag, params RscpValue[] children) => new(tag, 14,
        children.SelectMany(RscpProtocol.EncodeValue).ToArray());
    internal decimal Number() => Type switch
    {
        1 or 3 when Data.Length == 1 => Data[0],
        2 when Data.Length == 1 => (sbyte)Data[0],
        5 when Data.Length == 2 => BinaryPrimitives.ReadUInt16LittleEndian(Data),
        6 when Data.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(Data),
        7 when Data.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(Data),
        10 when Data.Length == 4 => (decimal)BinaryPrimitives.ReadSingleLittleEndian(Data),
        _ => throw new InvalidDataException("Die RSCP-Antwort hat keinen gueltigen Zahlenwert.")
    };
    internal IReadOnlyList<RscpValue> Children() => Type == 14 ? RscpProtocol.DecodeValues(Data) :
        throw new InvalidDataException("Der erwartete RSCP-Container fehlt.");
    internal RscpValue Required(uint tag) => Children().SingleOrDefault(v => v.Tag == tag) ??
        throw new InvalidDataException("Ein benoetigter RSCP-Messwert fehlt.");
}

/// <summary>
/// RSCP uses Rijndael with a 256-bit block, which is not AES (128-bit blocks).
/// CBC state is independent for sending/receiving and persists across frames on one connection.
/// </summary>
internal sealed class RscpProtocol
{
    private readonly CbcBlockCipher encrypt = new(new RijndaelEngine(256));
    private readonly CbcBlockCipher decrypt = new(new RijndaelEngine(256));
    private readonly Stream stream;

    internal RscpProtocol(Stream stream, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length is < 1 or > 32)
            throw new ArgumentException("Der RSCP-Schluessel muss 1 bis 32 UTF-8-Bytes enthalten.");
        var padded = Enumerable.Repeat((byte)255, 32).ToArray();
        bytes.CopyTo(padded, 0);
        var parameters = new ParametersWithIV(new KeyParameter(padded), Enumerable.Repeat((byte)255, 32).ToArray());
        encrypt.Init(true, parameters);
        decrypt.Init(false, parameters);
        this.stream = stream;
    }

    internal async Task<RscpValue> ExchangeAsync(RscpValue request, uint responseTag, CancellationToken token)
    {
        var frame = Frame(EncodeValue(request));
        var ciphertext = new byte[(frame.Length + 31) / 32 * 32];
        var padded = new byte[ciphertext.Length];
        frame.CopyTo(padded, 0);
        for (var offset = 0; offset < padded.Length; offset += 32)
            encrypt.ProcessBlock(padded, offset, ciphertext, offset);
        await stream.WriteAsync(ciphertext, token);
        var response = (await ReadFrameAsync(token)).SingleOrDefault(v => v.Tag == responseTag)
            ?? throw new InvalidDataException("Die RSCP-Antwort passt nicht zur Anfrage.");
        if (response.Type == 255)
            throw new InvalidDataException("Die E3/DC-Anlage hat die RSCP-Abfrage abgelehnt.");
        return response;
    }

    internal async Task<IReadOnlyList<RscpValue>> ReadFrameAsync(CancellationToken token)
    {
        // TCP may split even the first cipher block into many packets. A short read is not a complete response.
        var first = new byte[32];
        await stream.ReadExactlyAsync(first, token);
        var plain = new byte[32];
        decrypt.ProcessBlock(first, 0, plain, 0);
        if (plain[0] != 0xE3 || plain[1] != 0xDC || plain[2] != 0 || (plain[3] & 15) != 1 || (plain[3] & 0xE0) != 0)
            throw new InvalidDataException("Der RSCP-Rahmenkopf ist ungueltig.");
        var hasCrc = (plain[3] & 16) != 0;
        if (!hasCrc) throw new InvalidDataException("Die RSCP-Antwort hat keine Pruefsumme.");
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(plain.AsSpan(16));
        var frameLength = 18 + payloadLength + 4;
        var paddedLength = (frameLength + 31) / 32 * 32;
        Array.Resize(ref plain, paddedLength);
        var remaining = new byte[paddedLength - 32];
        await stream.ReadExactlyAsync(remaining, token);
        for (var offset = 0; offset < remaining.Length; offset += 32)
            decrypt.ProcessBlock(remaining, offset, plain, offset + 32);
        if (Crc(plain.AsSpan(0, frameLength - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(frameLength - 4)))
            throw new InvalidDataException("Die RSCP-Pruefsumme stimmt nicht.");
        return DecodeValues(plain.AsSpan(18, payloadLength));
    }

    internal static byte[] EncodeValue(RscpValue value)
    {
        var data = new byte[7 + value.Data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value.Tag);
        data[4] = value.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), checked((ushort)value.Data.Length));
        value.Data.CopyTo(data, 7);
        return data;
    }

    internal static IReadOnlyList<RscpValue> DecodeValues(ReadOnlySpan<byte> data)
    {
        var values = new List<RscpValue>();
        while (!data.IsEmpty)
        {
            if (data.Length < 7) throw new InvalidDataException("Ein RSCP-Wert ist abgeschnitten.");
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data[5..]);
            if (length > data.Length - 7) throw new InvalidDataException("Die RSCP-Wertlaenge ist ungueltig.");
            values.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(data), data[4], data.Slice(7, length).ToArray()));
            data = data[(7 + length)..];
        }
        return values;
    }

    internal static byte[] Frame(byte[] payload)
    {
        var frame = new byte[18 + payload.Length + 4];
        frame[0] = 0xE3; frame[1] = 0xDC; frame[3] = 0x11;
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(4), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(16), checked((ushort)payload.Length));
        payload.CopyTo(frame, 18);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(frame.Length - 4), Crc(frame.AsSpan(0, frame.Length - 4)));
        return frame;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }
}
