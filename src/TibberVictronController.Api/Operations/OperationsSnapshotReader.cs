using System.Globalization;
using TibberVictronController.Api.Health;
using TibberVictronController.Api.JointBattery;
using TibberVictronController.Business.Decisions;
using TibberVictronController.Business.Models;
using TibberVictronController.Business.Operations;
using TibberVictronController.Dal.Victron;

namespace TibberVictronController.Api.Operations;

/// <summary>Directory of the database file; null for in-memory databases (tests), which have no volume and no backup.</summary>
public sealed record OperationsAlertPaths(string? DatabaseDirectory)
{
    /// <summary>Written by <c>scripts/backup/backup-linux.sh</c> after every complete run: UTC time and archive name.</summary>
    public const string BackupMarkerFileName = "backup-last-success";

    public static OperationsAlertPaths FromConnectionString(string connectionString)
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource == ":memory:" || dataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            return new((string?)null);
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        return new(string.IsNullOrEmpty(directory) ? null : directory);
    }
}

/// <summary>
/// Reads what the operations alerts judge. It only reads process state and the file system: no device access, and
/// nothing here may throw for a missing file or an unreadable volume (the value is then unknown and raises no alert).
/// </summary>
public sealed class OperationsSnapshotReader(JointBatteryExecutionState execution, JointBatteryRuntimeState planning,
    VictronMqttRuntimeStatus mqtt, OperationsAlertPaths paths)
{
    /// <summary>Free bytes on the volume of a directory; replaceable in tests.</summary>
    public Func<string, long?> FreeBytes { get; init; } = ReadFreeBytes;

    /// <param name="jointExecutionExpected">Joint control is switched on and not in dry-run.</param>
    /// <param name="observedSinceUtc">Start of observation; stands in for the last Victron message while none was received.</param>
    public OperationsSnapshot Read(DateTimeOffset nowUtc, bool jointExecutionExpected, DateTimeOffset observedSinceUtc)
    {
        var current = execution.Current;
        // Same derivation as /health: blocked, restoring or silent while owning the batteries is not healthy.
        // "Disabled" and "Shadow" are healthy there because the switch may be off; here the switch is known to be on.
        var health = HealthEndpoints.BuildJointExecutionComponent(current, nowUtc);
        var controlling = health.Status == "Healthy" && current.Status is not ("Disabled" or "Shadow");
        return new OperationsSnapshot
        {
            JointExecutionExpected = jointExecutionExpected,
            JointExecutionControlling = controlling,
            JointExecutionStatusText = health.Message,
            JointPlanMissing = !HasUsablePlan(planning.Current.Plan, nowUtc),
            VictronMqttExpected = mqtt.ConnectionState != "NotStarted",
            // Set only when a message was received and parsed; "Connected" alone says nothing about data.
            VictronLastMessageAtUtc = mqtt.LastSuccessfulMessageAtUtc ?? observedSinceUtc,
            DatabaseVolumeFreeBytes = paths.DatabaseDirectory is { } directory ? FreeBytes(directory) : null,
            LastBackupSuccessAtUtc = paths.DatabaseDirectory is { } backupDirectory ? ReadBackupMarker(Path.Combine(backupDirectory, OperationsAlertPaths.BackupMarkerFileName)) : null
        };
    }

    // The conditions under which JointBatteryTickSelector falls back to self-consumption for lack of a plan. A quarter
    // hour already committed keeps running without a plan, so the fallback itself can begin up to 15 minutes later.
    private static bool HasUsablePlan(JointBatteryPlan? plan, DateTimeOffset nowUtc) =>
        plan is not null && nowUtc - plan.CalculatedAtUtc <= JointBatteryTickSelector.PlanMaximumAge &&
        plan.Slots.Any(slot => slot.TimeSlot.StartsAtUtc <= nowUtc && slot.TimeSlot.EndsAtUtc > nowUtc);

    private static long? ReadFreeBytes(string directory)
    {
        try
        {
            // Linux reports the file system that contains the path; Windows needs the drive root.
            var name = OperatingSystem.IsWindows() ? Path.GetPathRoot(Path.GetFullPath(directory)) : directory;
            return string.IsNullOrEmpty(name) ? null : new DriveInfo(name).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Time of the last complete backup; null when the marker does not exist (backup not installed).</summary>
    private static DateTimeOffset? ReadBackupMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var first = File.ReadLines(path).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var written))
                return written.ToUniversalTime();
            // Unexpected content: the file is still written at the end of a complete run.
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
