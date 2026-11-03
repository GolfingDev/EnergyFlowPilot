using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TibberVictronController.Dal.WebPush;

/// <summary>
/// Web Push without a third-party library: payload encryption "aes128gcm" (RFC 8291 with RFC 8188) and the VAPID
/// token (RFC 8292). Only .NET cryptography is used; the test checks the RFC 8291 example.
/// </summary>
public static class WebPushCrypto
{
    private const int RecordSize = 4096;

    /// <summary>Encrypts one message for a subscription (keys from the browser, base64url).</summary>
    public static byte[] Encrypt(byte[] payload, string p256dh, string auth, ECDiffieHellman? senderKey = null, byte[]? salt = null)
    {
        var userAgentPublic = Base64Url.Decode(p256dh);
        var authSecret = Base64Url.Decode(auth);
        if (userAgentPublic.Length != 65 || userAgentPublic[0] != 0x04) throw new ArgumentException("Ungueltiger Browser-Schluessel (p256dh).");
        if (authSecret.Length != 16) throw new ArgumentException("Ungueltiges Browser-Geheimnis (auth).");
        // One record only: Web Push messages must stay below 4 KB anyway.
        if (payload.Length > RecordSize - 17 - 103) throw new ArgumentException("Die Push-Nachricht ist zu lang.");

        using var ownKey = senderKey is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var sender = senderKey ?? ownKey!;
        var senderPublic = PublicPoint(sender.ExportParameters(false));
        using var userAgent = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublic[1..33], Y = userAgentPublic[33..65] }
        });
        var sharedSecret = sender.DeriveRawSecretAgreement(userAgent.PublicKey);

        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), userAgentPublic, senderPublic);
        var inputKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        salt ??= RandomNumberGenerator.GetBytes(16);
        var contentKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, 16, salt, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, 12, salt, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        // 0x02 marks the last (and only) record; no padding.
        var plain = Concat(payload, [0x02]);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(contentKey, 16)) aes.Encrypt(nonce, plain, cipher, tag);

        var header = new byte[21 + senderPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)senderPublic.Length;
        senderPublic.CopyTo(header, 21);
        return Concat(header, cipher, tag);
    }

    /// <summary>VAPID JWT (ES256) for the push service of the endpoint.</summary>
    public static string VapidToken(ECDsa key, string endpoint, string subject, DateTimeOffset expiresAtUtc)
    {
        var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = expiresAtUtc.ToUnixTimeSeconds(), sub = subject }));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{Base64Url.Encode(signature)}";
    }

    /// <summary>Uncompressed P-256 point (65 bytes), the form browsers expect as applicationServerKey.</summary>
    public static byte[] PublicPoint(ECParameters parameters) => Concat([0x04], parameters.Q.X!, parameters.Q.Y!);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

public static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var normal = text.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(normal.PadRight(normal.Length + (4 - normal.Length % 4) % 4, '='));
    }
}
