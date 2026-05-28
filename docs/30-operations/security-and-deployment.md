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
- Worker processes, cron jobs or scheduled control loops should catch unexpected production exceptions and send a clearly formatted failure email with enough context to identify the failing component, time and error cause quickly.
- Victron MQTT telemetry should be read as a live stream and not as a stale cache layer.
- Tibber and weather forecast fetches may use coarse caching, for example several hours or even one daily refresh where the provider update cadence allows it.

## Automatic Linux Updates

The Raspberry Pi deployment can be updated from Git by cron. The periodic job should run `scripts/deploy/auto-update-linux.sh`; that script fetches the configured branch, compares the local checkout with `origin/<branch>`, and only calls `scripts/deploy/update-linux.sh` when a fast-forward update is available.

Example root crontab entry:

```cron
*/5 * * * * APP_NAME=energyflowpilot GIT_BRANCH=develop /opt/tibber-victron-controller/scripts/deploy/auto-update-linux.sh
```

The auto updater writes to `/var/log/<app-name>/auto-update.log` by default and uses `/var/lock/<app-name>-auto-update.lock` to prevent overlapping deployments. Local uncommitted changes stop the update, so production-only configuration must stay in `/etc/<app-name>/api.env`, the SQLite database or environment variables rather than in tracked files.

The cron file can also be installed through the helper script:

```bash
sudo APP_NAME=energyflowpilot GIT_BRANCH=develop CRON_SCHEDULE="*/5 * * * *" ./scripts/deploy/install-auto-update-cron.sh
```
