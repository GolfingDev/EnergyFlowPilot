using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Models;

namespace TibberVictronController.Dal.E3dc;

/// <summary>Access data for one RSCP login. Deliberately not a record, so secrets never appear in a generated ToString().</summary>
internal sealed class E3dcRscpAccess
{
    private E3dcRscpAccess(string host, int port, string user, string password, string key)
    {
        Host = host; Port = port; User = user; Password = password; Key = key;
        // Only a hash is kept for change detection; the session must reconnect when any access value changes.
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', host, port, user, password, key))));
    }

    internal string Host { get; }
    internal int Port { get; }
    internal string User { get; }
    internal string Password { get; }
    internal string Key { get; }
    internal string Fingerprint { get; }

    internal static async Task<E3dcRscpAccess> LoadAsync(IControllerSettingStore settings, CancellationToken token)
    {
        async Task<string> Required(string key) => (await settings.GetSettingAsync(key, token))?.Value is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Der lokale E3/DC-Zugang ist nicht vollstaendig konfiguriert.");
        var host = await Required(ControllerSettingDefaults.E3dcRscpHostKey);
        var user = await Required(ControllerSettingDefaults.E3dcRscpUserKey);
        var password = await Required(ControllerSettingDefaults.E3dcRscpPasswordKey);
        var key = await Required(ControllerSettingDefaults.E3dcRscpKeyKey);
        var port = int.Parse(await Required(ControllerSettingDefaults.E3dcRscpPortKey), CultureInfo.InvariantCulture);
        return new(host, port, user, password, key);
    }
}

internal sealed class E3dcRscpConnection(TcpClient client, RscpProtocol protocol) : IDisposable
{
    internal RscpProtocol Protocol => protocol;
    public void Dispose() => client.Dispose();

    internal static async Task<E3dcRscpConnection> OpenAsync(E3dcRscpAccess access, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(access.Host, access.Port, token);
            var protocol = new RscpProtocol(client.GetStream(), access.Key);
            var auth = await protocol.ExchangeAsync(RscpValue.Container(1,
                new(2, 13, Encoding.UTF8.GetBytes(access.User)), new(3, 13, Encoding.UTF8.GetBytes(access.Password))), 0x00800001, token);
            // The AUTH response carries the granted user level; 0 means no access.
            var level = auth.Number();
            if (level <= 0)
                throw new InvalidDataException($"Die RSCP-Anmeldung wurde abgelehnt (Benutzerstufe {level}). RSCP-Benutzer und -Passwort (Portal-Zugang) pruefen.");
            return new(client, protocol);
        }
        catch { client.Dispose(); throw; }
    }
}
