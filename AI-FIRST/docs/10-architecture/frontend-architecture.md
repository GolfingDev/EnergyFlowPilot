# Frontend Architecture

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
