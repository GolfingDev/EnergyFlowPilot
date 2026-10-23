using TibberVictronController.Business.Abstractions;

namespace TibberVictronController.Dal.E3dc;

/// <summary>
/// Shares one authenticated RSCP connection between telemetry reads and power commands.
/// A new login per command (about two per second during joint control) overloads the device's few session slots.
/// </summary>
public sealed class E3dcRscpSession : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private E3dcRscpConnection? connection;
    private string? fingerprint;
    private bool disposed;

    internal async Task<T> ExecuteAsync<T>(IControllerSettingStore settings,
        Func<RscpProtocol, CancellationToken, Task<T>> exchange, CancellationToken token)
    {
        // RSCP is strictly request/response with chained CBC state, so exchanges must never interleave.
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var access = await E3dcRscpAccess.LoadAsync(settings, token);
            if (connection is not null && fingerprint != access.Fingerprint) Reset();
            if (connection is not null)
            {
                try { return await exchange(connection.Protocol, token); }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // The device may have closed an idle session. One fresh login is allowed; the
                    // repeated request is idempotent (reads, or the same ephemeral power command).
                    Reset();
                }
            }
            connection = await E3dcRscpConnection.OpenAsync(access, token);
            fingerprint = access.Fingerprint;
            return await exchange(connection.Protocol, token);
        }
        catch
        {
            // A partially completed exchange leaves the CBC chain undefined; the next call must log in again.
            Reset();
            throw;
        }
        finally { gate.Release(); }
    }

    private void Reset()
    {
        connection?.Dispose();
        connection = null;
        fingerprint = null;
    }

    public void Dispose()
    {
        disposed = true;
        Reset();
    }
}
