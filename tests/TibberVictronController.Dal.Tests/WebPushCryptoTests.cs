using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TibberVictronController.Dal.WebPush;

namespace TibberVictronController.Dal.Tests;

public sealed class WebPushCryptoTests
{
    [Fact]
    public void EncryptionMatchesTheExampleOfRfc8291()
    {
        // RFC 8291, Appendix A: fixed sender key and salt give exactly the published message.
        var senderPublic = Base64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var sender = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"),
            Q = new ECPoint { X = senderPublic[1..33], Y = senderPublic[33..65] }
        });

        var body = WebPushCrypto.Encrypt(Encoding.ASCII.GetBytes("When I grow up, I want to be a watermelon"),
            "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4", "BTBZMqHH6r4Tts7J_aSIgg",
            sender, Base64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw"));

        Assert.Equal("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN",
            Base64Url.Encode(body));
    }

    [Fact]
    public void VapidTokenIsSignedForThePushServiceOrigin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var expires = new DateTimeOffset(2026, 11, 3, 20, 0, 0, TimeSpan.Zero);

        var token = WebPushCrypto.VapidToken(key, "https://fcm.googleapis.com/fcm/send/abc", "https://pi.example.ts.net", expires);

        var parts = token.Split('.');
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var claims = JsonDocument.Parse(Base64Url.Decode(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(expires.ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
    }
}
