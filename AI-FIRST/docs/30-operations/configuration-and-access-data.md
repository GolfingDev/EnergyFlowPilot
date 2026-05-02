# Configuration And Access Data

## Rule

All runtime settings and all required access data must be stored in the database.

These values must be editable through the frontend in a future step, using DTO-based API contracts.

Every setting must have a meaningful default value in the application default catalog.

If the database does not exist yet, it must be created and seeded with all known default settings.

This rule applies to settings known today and to settings added in the future. Adding a new setting requires adding its default definition at the same time.

For sensitive access data, the meaningful default value is `null`, which means `not configured`.

## Runtime Settings

Runtime settings include values such as:

- total battery capacity in kWh
- minimum battery state of charge
- planning minimum battery state of charge
- target end battery state of charge
- maximum charge power
- maximum discharge power
- efficiency assumptions
- target end state of charge reserve
- temporary battery state of charge until Victron telemetry is connected
- average daily consumption forecast value
- consumption forecast timezone
- forecast horizon
- decision log retention in days
- feed-in compensation price per kWh
- controller behavior thresholds
- MQTT/Victron connection settings
- Tibber API endpoint and home selection
- weather provider settings
- PV forecast provider, location, peak power, module declination, module azimuth and forecast timezone

## Access Data

Access data includes values such as:

- Tibber access token
- MQTT username
- MQTT password
- weather API access data if needed later
- other external provider tokens or credentials

## Security Rules

- Access data must never be stored in source-controlled files.
- Access data must never be logged in plain text.
- Access data must never be returned to the frontend in plain text.
- The frontend may update access data, but read APIs must return only metadata such as `isConfigured`.
- Sensitive values should be protected before being persisted where practical.
- The SQLite database file must be protected by operating system permissions on the Raspberry Pi.

## API Rules

- Frontend communication uses DTOs only.
- DTOs for sensitive settings must support write/update without exposing the current value.
- A read DTO may show whether a value is configured, but not the stored secret.

## Persistence Rules

- Database records for settings must include a stable key.
- Sensitive settings must be marked as sensitive.
- Updates should store `UpdatedAtUtc`.
- Unknown setting keys must not be silently ignored.
- Database creation must seed all default setting definitions.
- Database startup checks must add missing future default settings without overwriting existing user values.
