# Configuration And Access Data

## Settings UI: daily controls and setup (21.10.2026)

- The default view is **Alltagssteuerung** with **Ladestrategie**,
  **Batteriegrenzen** and **Warmwasser**. The initial section is Ladestrategie.
- Battery minimum savings is now visible as **Mindestvorteil fuer den Akku**.
  Battery and hot-water minimum savings are distinct controls.
- Monetary inputs use **ct/kWh**. For example, 3 ct/kWh is still saved as
  0.03 EUR/kWh. Drafts remain in backend units; opening a page or toggling
  modes does not convert or save anything.
- **Erweiterter Modus** reveals setup values, provider selection, credentials,
  technical battery data, physical safety minimum, PV configuration, connections,
  polling/retention, MQTT and SMTP. Unclassified new fields are setup-only.
  Safety relevance and everyday visibility are separate classifications.
- A configured secret shows `********` as an always-visible placeholder and
  **Gespeichert**; an absent secret shows **Noch nicht eingerichtet**.
  The actual input stays empty until a replacement is typed. No secret prefix
  is exposed and the placeholder is never saved. Blank input keeps an existing
  secret. Read DTOs still contain no secret values.
- Mode changes preserve all drafts. Hidden unsaved setup edits are counted in
  a notice and remain included in Save; Reset also resets hidden drafts.
- Missing supplier credentials produce a setup warning, without blocking unrelated
  daily changes. Editing supplier/access settings still validates the required
  credentials. Hidden numeric validation failures remain labelled and can be
  reached by enabling the extended mode.
- A deep link to a setup-only section or the Hager OAuth callback enables the
  extended mode. This is a presentation choice, not access control.
- Existing stored values, API units and backend control logic are unchanged.

Checks: `node --experimental-strip-types --test tests/settingsPresentation.test.mjs`
and `npm run build` in the frontend directory. No new test framework is required.

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
- planning maximum battery state of charge for PV forecast headroom during grid charging
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
