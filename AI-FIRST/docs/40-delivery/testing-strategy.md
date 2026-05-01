# Testing Strategy

## Principles

- Tests before business logic where practical.
- Start with focused unit tests.
- Broaden coverage when shared behavior or user-facing workflows are touched.

## Backend Test Layers

- `Business.Tests`
  - decision engine
  - domain rules
  - time behavior
  - explainability requirements

- `Dal.Tests`
  - EF Core mappings
  - repository behavior
  - SQLite integration tests

- `Api.Tests`
  - endpoint contracts
  - health/status behavior
  - dependency composition where useful

## External Systems

- Do not call real Tibber API in unit tests.
- Do not require real MQTT/Victron hardware in tests.
- Use fakes, mocks or controlled test doubles.

## First Priority

The first meaningful business tests should cover the Battery Decision Engine before its production logic is implemented.

## Decision Audit Harness

The Decision Audit Harness is the review tool for challenging Battery Decision Engine behavior with synthetic and later real day data.

It must support:

- Golden scenario tests with 24 hours and 96 continuous 15-minute slots.
- Invariant tests for technical safety rules that must always hold.
- Metamorphic tests that compare behavior after controlled input changes.
- A `BaselineDecisionEngine` that provides a simple self-consumption comparison strategy.
- CSV and JSON export for user-readable review and external comparison tools.

Every audited decision slot must include:

- action
- rule id
- reason
- alternative action
- alternative rejected reason
- constraint flags where a safety or planning constraint influenced the decision

Golden scenario assertions must verify more than aggregate metrics:

- 96 slots in a continuous 15-minute grid
- state of charge never below minimum and never above maximum
- no empty or action-inconsistent reasons
- negative prices lead to grid charging when capacity is available and no later negative slot is strictly cheaper
- battery energy is used before future negative-price windows when consumption and state of charge allow it
- round-trip efficiency visibly changes state-of-charge progression
- configured end reserve is explicit in rule id and reason

Audit metrics must include:

- total cost
- grid import
- PV utilization
- charged and discharged energy
- cycle count
- minimum and maximum state of charge
- net benefit after efficiency loss

Manual audit exports are written to:

- `artifacts/decision-audit/golden-scenario.csv`
- `artifacts/decision-audit/golden-scenario.json`

The artifact directory is intentionally ignored by Git, because the files are generated review output.
