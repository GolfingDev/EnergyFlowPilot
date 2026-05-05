# Frontend Architecture

## Product Name

The user-facing application name is EnergyFlowPilot.

The product claim is: Smart energy flow control for your home.

## Framework

The frontend is a Vue.js application.

Vuetify is the preferred UI component library for the controller dashboard.

## API Contract

The frontend communicates with the backend only through DTO-based HTTP APIs.

It must not contain Battery Decision Engine business logic.

## Local Development

The Vite development server runs on port `5173`.

During local development, `/api` and `/health` are proxied to the ASP.NET Core API on `http://localhost:5094`.

## Sensitive Settings

Sensitive values may be written through the UI, but must never be displayed after saving.

Read views show only whether a sensitive setting is configured.

## Settings UX Rules

Settings pages use a calm three-zone layout: section navigation, one active detail section, and a compact summary.

Do not render all setting sections underneath each other.

Do not build one card per field. Use compact setting rows inside a section instead.

Do not add category chips to every field. Safety-critical settings use a subtle visual marker only.

Do not add gradients, glassmorphism, or loud decorative effects to settings surfaces.
