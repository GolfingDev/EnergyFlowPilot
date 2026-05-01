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
