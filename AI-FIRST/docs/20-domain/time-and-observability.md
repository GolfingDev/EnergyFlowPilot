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
