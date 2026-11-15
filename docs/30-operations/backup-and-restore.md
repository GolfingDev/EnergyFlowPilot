# Backup and Restore

State 2026-11-15: installed on the Pi, timer active, upload to OneDrive (`onedrive:EnergyFlowPilot-Backup`, a
business drive) working since 20:04. An archive downloaded again from OneDrive was identical to the local one and
restored completely.

- First run 2026-11-15 19:52: 7 s, archive 2.2 MB, I/O wait at most 3 %, the API service kept running (`NRestarts`
  unchanged, no error events).
- Restore drill on the Pi the same evening: all 18 saved tables restored with matching row counts (largest:
  `HomematicClimateSamples`, 151,740 rows), integrity check passed; settings, savings days, price archive, energy
  quarter hours, trips and the schema compared equal to the live database. Not yet done: starting the application on
  a restored database.
- The timer runs the script from the Pi's Git checkout (`/home/torsten/src/EnergyFlowPilot/scripts/backup/`), so it
  follows updates.

## Why

Settings, the data protection key ring, the sign-in environment file and the permanent history (energy quarter hours,
savings days, price archive, charging sessions, trips) exist only on the Pi's SD card. The history cannot be
recalculated: the raw samples it was built from are deleted after `telemetry.liveSampleRetentionDays`.

## What is saved

`scripts/backup/backup-linux.sh` writes one encrypted archive per night:

| Content | Source |
|---|---|
| Schema of all tables and indexes | `sqlite_master` |
| Data of every table except the excluded ones | one `INSERT` file per table, gzip |
| Row count per saved table | `tables.tsv`, checked by the restore |
| Data protection key ring | `/var/lib/energyflowpilot/data-protection-keys` |
| Sign-in environment file | `/etc/energyflowpilot/api.env` |

Excluded by default (`EXCLUDED_TABLES`): `LiveConsumptionSamples`, `DecisionLogEntries`, `DecisionLogReasons`,
`OperationalEvents`. They make up most of the database, have their own retention and are raw material, not results.
A table added to the application later is saved automatically, because the script saves every table that is not
excluded. A new table of raw data must be added to the exclusion list.

Consequence after a restore: the excluded tables are empty. The consumption profile, the price comparisons that read
the decision log and the PV correction relearn from new data; stored history and settings are complete.

## Why not a copy of the database file

`sqlite3 .backup` of the running database drove the service into watchdog restarts on 2026-11-04. The script never
copies the file. It reads each table in its own short read transaction with idle I/O priority (`nice`, `ionice -c3`);
the database runs in WAL mode, where a reader does not block the control loop's writes. Do not replace this by
`.backup`, `VACUUM INTO` or a file copy.

## Encryption and storage

The archive contains access tokens in plain text and the key ring that decrypts the RSCP credentials. It is encrypted
on the Pi with `gpg` (AES-256, symmetric) before anything is uploaded. The passphrase lies in
`/etc/energyflowpilot/backup.passphrase` (root only) and **must also be kept outside the Pi**, in the operator's
password manager: without it no archive can be read, and after an SD card failure the file on the Pi is gone.

Upload uses `rclone`, so any storage rclone supports works (OneDrive is the intended one). Kept:

- on the Pi: 7 days (`LOCAL_KEEP_DAYS`), in `/var/lib/energyflowpilot/backups/offsite`;
- remote `daily/`: 30 days (`REMOTE_DAILY_KEEP_DAYS`);
- remote `monthly/`: the first archive of each month, 800 days (`REMOTE_MONTHLY_KEEP_DAYS`).

Old archives are deleted only after the new one has arrived. Without `RCLONE_REMOTE` the archive stays on the Pi,
which does not protect against a dead SD card; the script says so in its log.

## Installation (operator, on the Pi)

```bash
sudo apt-get install rclone
sudo ./scripts/backup/install-backup-timer.sh
```

The installer creates `/etc/energyflowpilot/backup.env`, creates the passphrase once (it never replaces an existing
one) and enables the timer `energyflowpilot-backup.timer` (nightly at 03:20 with up to 10 minutes of random delay).
Then:

1. Copy the passphrase into the password manager: `sudo cat /etc/energyflowpilot/backup.passphrase`.
2. Sign in to the storage. The Pi has no browser; forward rclone's sign-in port to the PC instead of installing
   rclone there: `ssh -t -L 53682:localhost:53682 <user>@<pi> sudo rclone config --config /etc/energyflowpilot/rclone.conf`,
   new remote named `onedrive`, answer "Use auto config?" with `y` and open the printed `http://127.0.0.1:53682/auth…`
   link in the PC's browser.
3. Set `RCLONE_REMOTE=onedrive:EnergyFlowPilot-Backup` in `/etc/energyflowpilot/backup.env`.
4. First run by hand, watching the service: `sudo systemctl start energyflowpilot-backup.service`, then
   `journalctl -u energyflowpilot-backup.service -n 30` and `systemctl status energyflowpilot-api.service`
   (no restart, `NRestarts` unchanged).

## Monitoring

- `journalctl -u energyflowpilot-backup.service`: every run logs start, archive size, upload target and end.
- `/var/lib/energyflowpilot/backups/offsite/last-success`: time and name of the last complete run.
- A failed run leaves the systemd unit in state `failed` (`systemctl --failed`). There is no push or mail alert yet.

## Restore

`scripts/backup/restore-linux.sh <archive.tar.gpg> <empty-directory>` decrypts an archive and builds a new database
file from it. It checks every table's row count against the archive, runs SQLite's integrity check and switches the
file to WAL. It never touches the running service. It runs on the Pi or on any Linux machine with `sqlite3` and `gpg`.

Restore drill (do once after the first backups, then after larger schema changes): download an archive, run the
script into a temporary directory, compare a few values (a setting, the newest `EnergyQuarterHours` row, the savings
of a known day) with the live system.

Putting a restored state into operation (new SD card, or deliberate roll-back):

1. Set up the Pi with `scripts/deploy/setup-raspberry.sh` and stop the service.
2. Copy the restored database to `/var/lib/energyflowpilot/tibber-victron-controller.db`, the directory
   `data-protection-keys` next to it and `api.env` to `/etc/energyflowpilot/`; owner `energyflowpilot` for the data
   directory, `root:energyflowpilot` mode `0640` for `api.env`.
3. Before starting: joint control and the wallbox and hot water switches come back as they were saved, i.e. active.
   Check `victron.dryRun`, `jointBattery.executionEnabled`, `ev.charging.controlEnabled` and
   `weishauptHotWater.enabled` in `ControllerSettings` and switch off what should not start at once.
4. Start the service and check `/health` and the dashboard.

## Not covered

- The repository itself (GitHub) and the published binaries (rebuilt by the deploy script).
- Raw samples, decision log and operational events (see above).
- The rclone sign-in (`rclone.conf`): after an SD card failure the storage is authorised again.
