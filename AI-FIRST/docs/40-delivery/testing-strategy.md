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

Audit metrics must include:

- total cost
- grid import
- PV utilization
- charged and discharged energy
- cycle count
- minimum and maximum state of charge
- net benefit after efficiency loss
