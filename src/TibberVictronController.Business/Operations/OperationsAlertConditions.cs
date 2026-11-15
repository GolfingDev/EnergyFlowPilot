using System.Globalization;

namespace TibberVictronController.Business.Operations;

/// <summary>
/// What the alert service observed at one moment. Values that cannot be read are null and raise no alert: an alert
/// about a problem that may not exist would teach people to ignore the real ones.
/// </summary>
public sealed record OperationsSnapshot
{
    /// <summary>Joint control is meant to control hardware (switched on, not in dry-run).</summary>
    public bool JointExecutionExpected { get; init; }

    public bool JointExecutionControlling { get; init; }

    /// <summary>Fixed status text of the health derivation; never a device or exception message.</summary>
    public string? JointExecutionStatusText { get; init; }

    /// <summary>The joint execution controls, but there is no plan it could follow.</summary>
    public bool JointPlanMissing { get; init; }

    /// <summary>False while the MQTT client was never started; then silence is no fault.</summary>
    public bool VictronMqttExpected { get; init; }

    /// <summary>Last received Victron message, or the start of observation when none was received yet.</summary>
    public DateTimeOffset? VictronLastMessageAtUtc { get; init; }

    public long? DatabaseVolumeFreeBytes { get; init; }

    /// <summary>Null when no backup has ever completed, i.e. the backup is not installed.</summary>
    public DateTimeOffset? LastBackupSuccessAtUtc { get; init; }
}

/// <summary>Turns a snapshot into the problems present now, with their German texts, hold times and stable ids.</summary>
public static class OperationsAlertConditions
{
    public const string JointExecutionNotControllingId = "joint-execution-not-controlling";
    public const string JointPlanMissingId = "joint-plan-missing";
    public const string VictronMqttSilentId = "victron-mqtt-silent";
    public const string DatabaseDiskLowId = "database-disk-low";
    public const string BackupOverdueId = "backup-overdue";

    public static readonly TimeSpan JointExecutionHoldTime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan JointPlanHoldTime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan VictronMqttSilenceLimit = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BackupMaximumAge = TimeSpan.FromHours(36);
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;

    // The execution retries every 30 seconds after a fault and may control for a few seconds in between; the plan is
    // recalculated every 30 seconds. One good sample in between must neither restart the hold time nor report recovery.
    private static readonly TimeSpan JointRecoveryTime = TimeSpan.FromMinutes(3);

    // Free space moves around the limit while logs rotate; that is one alert, not a series.
    private static readonly TimeSpan DiskRecoveryTime = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<OperationsProblem> Evaluate(OperationsSnapshot snapshot, DateTimeOffset nowUtc, TimeZoneInfo displayZone)
    {
        var problems = new List<OperationsProblem>();
        if (snapshot.JointExecutionExpected && !snapshot.JointExecutionControlling)
            problems.Add(new(JointExecutionNotControllingId, "Akkusteuerung steht",
                "Die gemeinsame Akkusteuerung ist eingeschaltet, steuert die Akkus aber nicht." +
                (string.IsNullOrWhiteSpace(snapshot.JointExecutionStatusText) ? "" : " Status: " + snapshot.JointExecutionStatusText),
                JointExecutionHoldTime, JointRecoveryTime));

        if (snapshot.JointExecutionExpected && snapshot.JointExecutionControlling && snapshot.JointPlanMissing)
            problems.Add(new(JointPlanMissingId, "Akkusteuerung ohne Plan",
                "Die gemeinsame Akkusteuerung hat keinen gültigen Plan und läuft im Notbetrieb Eigenverbrauch: keine Netzladung, keine Preisoptimierung.",
                JointPlanHoldTime, JointRecoveryTime));

        if (snapshot is { VictronMqttExpected: true, VictronLastMessageAtUtc: { } lastMessage } && nowUtc - lastMessage >= VictronMqttSilenceLimit)
            problems.Add(new(VictronMqttSilentId, "Keine Victron-Messwerte",
                "Vom Victron-System kommen keine MQTT-Nachrichten mehr.", TimeSpan.Zero, TimeSpan.Zero, lastMessage));

        if (snapshot.DatabaseVolumeFreeBytes is { } free && free < MinimumFreeBytes)
            problems.Add(new(DatabaseDiskLowId, "Speicherplatz knapp",
                string.Create(CultureInfo.GetCultureInfo("de-DE"),
                    $"Auf dem Laufwerk der Datenbank sind nur noch {free / (1024d * 1024 * 1024):0.0} GB frei (Grenze 2 GB)."),
                TimeSpan.Zero, DiskRecoveryTime));

        if (snapshot.LastBackupSuccessAtUtc is { } backup && nowUtc - backup > BackupMaximumAge)
            problems.Add(new(BackupOverdueId, "Datensicherung überfällig",
                $"Die letzte erfolgreiche Datensicherung ist vom {OperationsAlertMessage.LocalTime(backup, displayZone)}.",
                TimeSpan.Zero, TimeSpan.Zero, backup + BackupMaximumAge));

        return problems;
    }
}

/// <summary>Texts of the push messages. Times are shown in the display zone (Europe/Berlin); this is a display boundary.</summary>
public static class OperationsAlertMessage
{
    public sealed record Text(string Title, string Body);

    public static Text Compose(OperationsAlertNotification notification, TimeZoneInfo displayZone)
    {
        var alert = notification.Alert;
        var since = LocalTime(alert.SinceUtc, displayZone);
        return notification.Kind switch
        {
            OperationsAlertNotificationKind.End => new("Wieder in Ordnung: " + alert.Title,
                $"{alert.Title}: wieder in Ordnung seit {LocalTime(alert.EndedAtUtc ?? alert.SinceUtc, displayZone)}. Die Störung bestand seit {since}."),
            OperationsAlertNotificationKind.Reminder => new(alert.Title, $"Besteht weiterhin: {alert.Detail} Störung seit {since}."),
            _ => new(alert.Title, $"{alert.Detail} Störung seit {since}.")
        };
    }

    /// <summary>Day, month and time: a reminder or a recovery may come on another day than the beginning.</summary>
    public static string LocalTime(DateTimeOffset utc, TimeZoneInfo displayZone) =>
        TimeZoneInfo.ConvertTime(utc, displayZone).ToString("dd.MM. HH:mm 'Uhr'", CultureInfo.InvariantCulture);
}
