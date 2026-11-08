# AGENTS.md

Instructions for AI coding agents and developers. Product name: **EnergyFlowPilot**.
It controls real home batteries (Victron, E3/DC), a wallbox and a heat pump from a Raspberry Pi.
Treat every hardware write path as safety-critical.

## Layout

```
Api  →  Business  ←  Dal        (Business has no project or package references; the compiler enforces it)
```

- `src/TibberVictronController.Business` – domain logic and all boundary interfaces (`Abstractions/`).
- `src/TibberVictronController.Dal` – EF Core/SQLite, external API clients, device protocols (RSCP, OCPP, Modbus, Homematic, Victron MQTT settings and snapshots).
- `src/TibberVictronController.Api` – endpoints, hosted services, DI (`Configuration/ControllerServiceCollectionExtensions.cs`). The Victron MQTT client (`VictronMqttClientService`) and the setpoint publisher (`MqttVictronSetpointPublisher`) live in `Api/Configuration/`.
- `src/TibberVictronController.Frontend` – Vue 3 + Vuetify 4. Display only, no business logic.
- A feature uses the same folder name in every layer: `EvCharging`, `JointBattery`, `Weishaupt`, `Homematic`, `Energy`, `Prices`, `Savings`.
- Requirements live in `docs/` (reading list: `docs/README.md`). Updated docs win over existing behaviour; make conflicts explicit before coding.

## Battery decision paths

- Joint control (Victron + E3/DC) is active when `jointBattery.executionEnabled=true` or `jointBattery.recoveryPending=true`:
  `Business/Decisions/JointBattery*` and `Api/JointBattery/`. The `JointAware*` decorators then bypass `CurrentBatteryDecisionService`.
- Otherwise: `CurrentBatteryDecisionService` (live decision) and `BatteryForecastSimulator` (forecast).
- Every decision is persisted with rule ID and reason.

## Commands (repository root)

Always name the solution: ignored legacy project files in a local root make a bare `dotnet build` fail.

- Backend tests: `dotnet test TibberVictronController.slnx`
- One test class: `dotnet test TibberVictronController.slnx --filter "FullyQualifiedName~<ClassName>"`
- Live tests against the real Tibber API run only with `RUN_LIVE_TESTS=1` and `TIBBER_ACCESS_TOKEN` set.
- Frontend (in `src/TibberVictronController.Frontend`): `npm ci`, then
  - type check: `npx vue-tsc --noEmit`
  - unit tests: `node --experimental-strip-types --test tests/settingsPresentation.test.mjs`
  - build (writes `dist/`): `npm run build`
- Run locally: `dotnet run --project src/TibberVictronController.Api` (port 5094; Vite dev server `npm run dev` on 5173 proxies `/api`).
  This connects to the devices configured in the local database. Check `victron.dryRun=true` first.

CI (`.github/workflows/ci.yml`) runs backend tests, type check and frontend unit tests on every push to `main` and on pull requests.

## Checks by change type

- C# code: backend tests. Frontend code: type check and frontend unit tests.
- New or changed setting: `ControllerSettingDefaults` (key, default, sensitivity), `ControllerSettingValueValidation`, `GuiMetadataCatalog`, and for numeric inputs the bounds in `Frontend/src/pages/settingsPresentation.ts`.
- Database: additive changes only. Put every new table or column into the EF model **and** into `Dal/Persistence/ControllerDbInitializer.cs` (existing databases are upgraded there). The project uses `EnsureCreated`, not EF migrations: do not run `dotnet ef migrations`.
- Hardware writes: every write path must honour its device switch – `victron.dryRun` (default `true`), `jointBattery.executionEnabled`, `evCharging.controlEnabled`, `weishauptHotWater.enabled` (all default off). The switch is checked by the caller, not by the adapter; `MqttVictronSetpointPublisher.PublishAsync` does not check `victron.dryRun` itself. Add a test with a fake adapter. Tests on real devices are done by the operator only.

## Rules

- Error messages for logs, exceptions, API responses and UI in German. Code, comments, commit messages and docs in English.
- API errors: `{ "message": "<German text>" }`.
- No business logic in endpoints; DTOs for every frontend exchange; never return sensitive setting values in read DTOs.
- Inject `IUtcClock` for time. UTC internally; convert to `Europe/Berlin` only at API/display boundaries.
- Interfaces at architectural boundaries only. Prefer parameter objects over more than 2–3 parameters.
- Comment non-trivial code with intent (why, not what).
- Secrets: never in the repository or logs. Only the keys listed in `Api/Configuration/ProtectedRscpSettingStore.cs` are encrypted at rest.
- Do not edit generated or local-only files: `bin/`, `obj/`, `dist/`, `node_modules/`, `artifacts/`, `*.db`, ignored legacy files in the repository root.

## Working agreement

- Plan before coding; work in small steps; change only files the task needs; no large refactorings unless asked.
- If tests fail, stop and report – do not iterate without user input.
- Run at most one build or test pass per task.
- Commit directly on `main`, only when asked. Push only when asked: the Raspberry Pi updates itself from Git.
- Do not touch the running Pi (service restarts, database backups, settings) without explicit permission.

## Done means

- The relevant checks above ran and passed; report command and result.
- Docs in `docs/` updated when behaviour or requirements changed.
- The hand-over names what was not verified, especially hardware behaviour.

## Deployment

The Raspberry Pi runs the API as systemd service `energyflowpilot-api.service`; nginx serves the built frontend and proxies `/api`, `/hubs` and `/health` to port 5094.
Scripts: `scripts/deploy/` (`update-linux.sh` publishes and restarts, `auto-update-linux.sh` runs it from cron when the tracked branch has new commits).
