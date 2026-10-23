using TibberVictronController.Business.Abstractions;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Api.Configuration;
using TibberVictronController.Dal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TibberVictronController.Api.JointBattery;

/// <summary>Runs outside the failed service process from systemd ExecStopPost/ExecStartPre.</summary>
public static class JointBatteryStopCommand
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            // A fresh installation must initialize its schema before it can have a recovery obligation.
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControllerDbContext>();
                var connection = db.Database.GetDbConnection();
                await connection.OpenAsync(timeout.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ControllerSettings'";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)) == 0) return 0;
            }
            while (!timeout.IsCancellationRequested)
            {
                await using var scope = services.CreateAsyncScope();
                var settings = scope.ServiceProvider.GetRequiredService<IControllerSettingStore>();
                if (!bool.TryParse((await settings.GetSettingAsync(ControllerSettingDefaults.JointBatteryRecoveryPendingKey, timeout.Token))?.Value, out var pending) || !pending)
                    return 0;
                if (await scope.ServiceProvider.GetRequiredService<JointBatteryExecutionCoordinator>().ReleaseAsync(timeout.Token))
                {
                    await settings.SaveSettingAsync(new(ControllerSettingDefaults.JointBatteryRecoveryPendingKey, "false",
                        ControllerSettingSensitivity.Normal, DateTimeOffset.UtcNow), timeout.Token);
                    return 0;
                }
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (Exception error)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("JointBatteryStop").LogError(
                "Gemeinsame Ruecknahme nicht bestaetigt; Neustart bleibt gesperrt. Fehlertyp={ErrorType}", error.GetType().Name);
        }
        finally
        {
            using var disconnect = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await services.GetRequiredService<VictronMqttClientService>().StopAsync(disconnect.Token); } catch { }
        }
        return 1;
    }
}
