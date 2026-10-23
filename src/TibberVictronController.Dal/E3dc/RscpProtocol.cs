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
    internal const byte ErrorType = 255;

    // Firmware versions differ in the numeric type they report for the same tag, so all RSCP number types are accepted.
    internal decimal Number() => Type switch
    {
        1 or 3 when Data.Length == 1 => Data[0],
        2 when Data.Length == 1 => (sbyte)Data[0],
        4 when Data.Length == 2 => BinaryPrimitives.ReadInt16LittleEndian(Data),
        5 when Data.Length == 2 => BinaryPrimitives.ReadUInt16LittleEndian(Data),
        6 when Data.Length == 4 => BinaryPrimitives.ReadInt32LittleEndian(Data),
        7 when Data.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(Data),
        8 when Data.Length == 8 => BinaryPrimitives.ReadInt64LittleEndian(Data),
        9 when Data.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(Data),
        10 when Data.Length == 4 && float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(Data)) =>
            (decimal)BinaryPrimitives.ReadSingleLittleEndian(Data),
        11 when Data.Length == 8 && double.IsFinite(BinaryPrimitives.ReadDoubleLittleEndian(Data)) =>
            (decimal)BinaryPrimitives.ReadDoubleLittleEndian(Data),
        ErrorType => throw new InvalidDataException($"Die E3/DC-Anlage meldet fuer Tag {RscpProtocol.TagName(Tag)} den Fehler {DescribeError()}."),
        _ => throw new InvalidDataException(
            $"Die RSCP-Antwort fuer Tag {RscpProtocol.TagName(Tag)} hat keinen gueltigen Zahlenwert (Typ {Type}, {Data.Length} Bytes).")
    };

    internal IReadOnlyList<RscpValue> Children() => Type switch
    {
        14 => RscpProtocol.DecodeValues(Data),
        ErrorType => throw new InvalidDataException($"Die E3/DC-Anlage meldet fuer Tag {RscpProtocol.TagName(Tag)} den Fehler {DescribeError()}."),
        _ => throw new InvalidDataException($"Fuer Tag {RscpProtocol.TagName(Tag)} wurde ein RSCP-Container erwartet, erhalten wurde Typ {Type}.")
    };

    internal RscpValue Required(uint tag) => Children().SingleOrDefault(v => v.Tag == tag) ??
        throw new InvalidDataException($"Der RSCP-Messwert {RscpProtocol.TagName(tag)} fehlt in der Antwort {RscpProtocol.TagName(Tag)}.");

    /// <summary>RSCP error payloads carry a numeric code; names follow the published RSCP tag list.</summary>
    internal string DescribeError()
    {
        if (Data.Length is not (1 or 2 or 4)) return "unbekannt";
        var code = Data.Length switch
        {
            1 => Data[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(Data),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(Data)
        };
        var name = code switch
        {
            1 => "NOT_HANDLED",
            2 => "ACCESS_DENIED (Benutzer, Passwort oder Berechtigung pruefen)",
            3 => "FORMAT",
            4 => "AGAIN",
            5 => "OUT_OF_BOUNDS",
            6 => "NOT_AVAILABLE",
            7 => "UNKNOWN_TAG",
            8 => "ALREADY_IN_USE",
            _ => "unbekannt"
        };
        return $"{code} {name}";
    }
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
        var values = await ReadFrameAsync(token);
        var response = values.SingleOrDefault(v => v.Tag == responseTag) ?? throw new InvalidDataException(
            $"Die RSCP-Antwort passt nicht zur Anfrage: erwartet {TagName(responseTag)}, erhalten " +
            $"{string.Join(", ", values.Select(v => $"{TagName(v.Tag)} (Typ {v.Type})"))}.");
        if (response.Type == RscpValue.ErrorType)
        {
            // Authentication failures arrive as an error value on the AUTH response tag.
            var hint = responseTag == 0x00800001 ? " Anmeldung abgelehnt: RSCP-Benutzer und -Passwort (Portal-Zugang) pruefen." : "";
            throw new InvalidDataException(
                $"Die E3/DC-Anlage hat die RSCP-Abfrage {TagName(request.Tag)} abgelehnt (Fehler {response.DescribeError()}).{hint}");
        }
        return response;
    }

    internal static string TagName(uint tag) => $"0x{tag:X8}";

    internal async Task<IReadOnlyList<RscpValue>> ReadFrameAsync(CancellationToken token)
    {
        // TCP may split even the first cipher block into many packets. A short read is not a complete response.
        var first = new byte[32];
        await stream.ReadExactlyAsync(first, token);
        var plain = new byte[32];
        decrypt.ProcessBlock(first, 0, plain, 0);
        if (plain[0] != 0xE3 || plain[1] != 0xDC || plain[2] != 0 || (plain[3] & 15) != 1 || (plain[3] & 0xE0) != 0)
            // A wrong RSCP key decrypts to random bytes, so the fixed frame header is the first thing that fails.
            throw new InvalidDataException("Der RSCP-Rahmenkopf ist ungueltig. Meist ist der RSCP-Schluessel falsch (am Geraet unter Personalisieren > Benutzerprofil gesetzt).");
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
