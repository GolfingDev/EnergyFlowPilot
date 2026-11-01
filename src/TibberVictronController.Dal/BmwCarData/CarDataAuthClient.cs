using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TibberVictronController.Dal.BmwCarData;

public sealed record CarDataDeviceFlow(
    string DeviceCode,
    string CodeVerifier,
    string UserCode,
    string VerificationUrl,
    TimeSpan PollInterval,
    DateTimeOffset ExpiresAtUtc);

public sealed record CarDataTokens(string? Gcid, string IdToken, string RefreshToken, TimeSpan ExpiresIn);

public enum CarDataPollOutcome
{
    Pending,
    SlowDown,
    Completed,
    Failed
}

public sealed record CarDataPollResult(CarDataPollOutcome Outcome, CarDataTokens? Tokens = null, string? Error = null);

/// <summary>A refresh token BMW no longer accepts; only a new sign-in helps.</summary>
public sealed class CarDataSignInRequiredException(string message) : Exception(message);

/// <summary>
/// OAuth device-code flow with PKCE against the BMW customer identity service. The client id comes from the
/// CarData client the owner creates in the BMW customer portal; there is no client secret.
/// </summary>
public sealed class CarDataAuthClient(HttpClient httpClient)
{
    public const string DeviceCodeUrl = "https://customer.bmwgroup.com/gcdm/oauth/device/code";
    public const string TokenUrl = "https://customer.bmwgroup.com/gcdm/oauth/token";
    public const string Scope = "authenticate_user openid cardata:api:read cardata:streaming:read";

    public async Task<CarDataDeviceFlow> StartAsync(string clientId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var response = await httpClient.PostAsync(DeviceCodeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
            ["response_type"] = "device_code",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"BMW lehnt die Anmeldung ab (HTTP {(int)response.StatusCode}{ErrorText(body)}). Client-ID und Abos (CarData API und Streaming) im BMW-Portal pruefen.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var userCode = Text(root, "user_code") ?? throw new InvalidOperationException("BMW liefert keinen Anmeldecode.");
        var url = Text(root, "verification_uri_complete") ?? Text(root, "verification_uri")
            ?? throw new InvalidOperationException("BMW liefert keine Anmeldeadresse.");
        return new CarDataDeviceFlow(
            Text(root, "device_code") ?? throw new InvalidOperationException("BMW liefert keinen Geraetecode."),
            verifier,
            userCode,
            url,
            TimeSpan.FromSeconds(Math.Clamp(Int(root, "interval") ?? 5, 1, 60)),
            nowUtc.AddSeconds(Math.Clamp(Int(root, "expires_in") ?? 600, 60, 3600)));
    }

    public async Task<CarDataPollResult> PollAsync(string clientId, CarDataDeviceFlow flow, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = flow.DeviceCode,
            ["code_verifier"] = flow.CodeVerifier
        }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode) return new CarDataPollResult(CarDataPollOutcome.Completed, ReadTokens(body, null));
        var error = OAuthError(body);
        return error switch
        {
            "authorization_pending" => new CarDataPollResult(CarDataPollOutcome.Pending),
            "slow_down" => new CarDataPollResult(CarDataPollOutcome.SlowDown),
            // Transient server faults: keep polling until the code expires.
            _ when (int)response.StatusCode >= 500 => new CarDataPollResult(CarDataPollOutcome.Pending),
            _ => new CarDataPollResult(CarDataPollOutcome.Failed, Error: $"BMW-Anmeldung fehlgeschlagen (HTTP {(int)response.StatusCode}{ErrorText(body)}).")
        };
    }

    public async Task<CarDataTokens> RefreshAsync(string clientId, string refreshToken, string? gcid, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var tokens = ReadTokens(body, refreshToken);
            return tokens with { Gcid = tokens.Gcid ?? gcid };
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CarDataSignInRequiredException($"BMW nimmt die gespeicherte Anmeldung nicht mehr an (HTTP {(int)response.StatusCode}{ErrorText(body)}). Bitte neu anmelden.");
        throw new HttpRequestException($"BMW-Tokenerneuerung fehlgeschlagen (HTTP {(int)response.StatusCode}).");
    }

    private static CarDataTokens ReadTokens(string body, string? previousRefreshToken)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        // BMW rotates the refresh token; without a new one the previous one stays valid.
        return new CarDataTokens(
            Text(root, "gcid"),
            Text(root, "id_token") ?? throw new InvalidOperationException("BMW liefert kein ID-Token (Scope openid fehlt?)."),
            Text(root, "refresh_token") ?? previousRefreshToken ?? throw new InvalidOperationException("BMW liefert kein Refresh-Token."),
            TimeSpan.FromSeconds(Math.Clamp(Int(root, "expires_in") ?? 3600, 300, 86400)));
    }

    private static string? OAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return Text(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Only the OAuth error code and description go into messages; the body may contain tokens.
    private static string ErrorText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var code = Text(document.RootElement, "error");
            var description = Text(document.RootElement, "error_description");
            return code is null ? string.Empty : $", {code}{(description is null ? string.Empty : ": " + description)}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
