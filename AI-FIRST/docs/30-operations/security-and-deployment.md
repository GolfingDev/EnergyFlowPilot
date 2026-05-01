# Security And Deployment

## Security Rules

- No secrets in the repository.
- Store runtime settings and required access data in the database.
- Example configuration may show keys but must not contain real values.
- Sensitive values must not be logged or returned to the frontend in plain text.
- The SQLite database file must be protected by operating system permissions on the Raspberry Pi.

## Hardware Rules

- Hardware access must always go through interfaces.
- Local development and tests must use fakes or mocks.
- No implementation should require real Victron hardware to run unit tests.

## Deployment Target

The production target is a Raspberry Pi.

Planned deployment components:

- ASP.NET Core service
- SQLite database
- MQTTnet 5 for Victron/MQTT communication
- Tibber API client
- systemd service
- nginx reverse proxy

## Operational Rules

- Startup failures should be explicit.
- Health information should be visible through API endpoints.
- Background workers should log degraded states.
