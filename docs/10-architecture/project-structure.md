# Project Structure

## Backend Projects

- `src/TibberVictronController.Api`
  - ASP.NET Core app
  - HTTP endpoints
  - background services
  - dependency injection setup

- `src/TibberVictronController.Business`
  - domain models
  - decision engine
  - use cases
  - interfaces for Tibber, Victron, time, persistence and commands

- `src/TibberVictronController.Dal`
  - EF Core SQLite database context
  - persistence entities
  - repository implementations
  - migrations

## Frontend Project

- `src/TibberVictronController.Frontend`
  - Vue.js application
  - dashboard UI
  - API-backed display only
  - no battery decision business logic

## Test Projects

- `tests/TibberVictronController.Business.Tests`
- `tests/TibberVictronController.Dal.Tests`
- `tests/TibberVictronController.Api.Tests`

## Naming Rule

Project and folder names should stay explicit. Avoid vague names such as `Core`, `Common` or `Shared` until a concrete need exists.
