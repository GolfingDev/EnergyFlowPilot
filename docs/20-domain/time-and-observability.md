# Time And Observability

## Time Rules

- Internal timestamps use UTC.
- Database timestamps use UTC.
- Scheduling logic uses UTC.
- Display and frontend-facing values may be converted to `Europe/Berlin`.
- Time access must be abstracted for testability.
- MQTT or Victron live telemetry is realtime input and must not be treated as a long-lived cache.
- Tibber and weather or PV forecast data may be cached for hours because their update frequency is much lower than live telemetry.

## Logging Rules

- No silent fallbacks.
- External-service failures must be logged.
- Hardware command failures must be logged.
- Data freshness problems must be logged.
- Battery decisions must be persisted with reasons.
- Every realtime Battery Decision Engine decision must be persisted with input summary, result, target power, rule names and structured reasons.
- Realtime discharge decisions must log the measured grid import used to prevent battery feed-in into the grid.
- Realtime export absorption decisions must log current grid export, configured feed-in compensation and any future negative-price reason for preserving battery headroom.
- Decision log retention must be configurable through persisted settings.
- Decision log cleanup must use UTC timestamps and must not delete entries newer than the configured retention period.

## Events

Technical and operational events should be stored when they are relevant for diagnosis, especially:

- Tibber API failures
- MQTT connection failures
- Victron telemetry parsing issues
- command execution failures
- stale price or telemetry data

## Operations Alerts

`OperationsAlertBackgroundService` (`Api/Operations/`) tells people when control or its safeguards silently stop. Once
a minute it reads process state and the file system (no device access, no hardware writes) and sends web push
messages. The rules are pure Business code (`Business/Operations/`).

| Alert (id) | Condition | Told after |
| --- | --- | --- |
| Akkusteuerung steht (`joint-execution-not-controlling`) | `jointBattery.executionEnabled=true` and `victron.dryRun=false`, but the joint execution does not control: the `/health` derivation for `jointExecution` is not `Healthy` (`Blocked`, `Restoring`, or owning the batteries without reporting for 30 s), or the status is `Disabled`/`Shadow` although the switch is on | 10 minutes |
| Akkusteuerung ohne Plan (`joint-plan-missing`) | the joint execution controls, but the plan is missing, older than 75 s or does not contain the current quarter hour, i.e. the self-consumption fallback | 15 minutes |
| Keine Victron-Messwerte (`victron-mqtt-silent`) | no Victron MQTT message received (not merely "connected") | 5 minutes |
| Speicherplatz knapp (`database-disk-low`) | less than 2 GB free on the volume of the database | at once |
| Datensicherung überfällig (`backup-overdue`) | `backup-last-success` in the database's directory is older than 36 hours; no file, no alert | at once |

- One message when an alert starts (what is wrong and since when, `Europe/Berlin`), a reminder every 6 hours while
  it lasts, one message when it is over ("Wieder in Ordnung"). Each alert has its own notification tag, so a
  reminder replaces the earlier notification; tapping it opens the dashboard.
- A problem shorter than its hold time is never told. The two joint alerts end only after 3 minutes without the
  problem, the disk alert after 10 minutes: an execution that fails, retries and fails again is one alert.
- Dry-run with joint control switched on is the deliberate shadow mode and does not alert.
- The fallback caused by estimated prices (no grid charging on estimates) has a plan and does not alert. A quarter
  hour already committed keeps running without a plan, so the fallback itself can begin up to 15 minutes after the
  plan went missing.
- Receivers: every device with notifications switched on. A push subscription stores the user but not the role
  (roles exist only in the sign-in session), so administrators cannot be addressed separately.
- A message that could not be delivered to any device is tried again every minute; delivered to at least one device
  counts as delivered.
- Every start and end is stored as an `OperationalEvent` of category `OperationsAlert` (reminders are not).
- The state lives in memory. After a restart of the service a problem that still exists alerts again after its
  hold time; a recovery that happened during the restart is not reported.
- Switch: `operations.alerts.enabled` (default `true`). Off sends nothing and clears the state.
- Not covered: a dead Raspberry Pi, a dead or hanging process, lost network or a failing push service cannot alert
  about themselves. That needs a check from outside (not built). The alerts also do not judge wallbox, heat pump or
  price sources.
