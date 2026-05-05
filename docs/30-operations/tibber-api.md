# Tibber API

## Purpose

The Tibber integration loads future electricity prices for Decision Engine forecast and decision rules.

## Access Data

The Tibber access token is stored in the database as a sensitive controller setting:

- key: `tibber.accessToken`
- default: `null`
- frontend read APIs must only expose whether it is configured

## Runtime Settings

The Tibber API endpoint and home selection are stored as normal controller settings:

- `tibber.apiEndpoint`
- `tibber.homeSelection`

The default home selection is `first`. A concrete home id may be configured later through the frontend.

## Query Behavior

- The client uses the Tibber GraphQL API through `HttpClient`.
- Prices are requested with `QUARTER_HOURLY` resolution.
- Returned prices must map to 15-minute forecast slots.
- If Tibber returns another interval, the client fails explicitly.
- The client does not call Tibber in unit tests.

## Live Integration Test

The optional live integration test may call the real Tibber API when these local environment variables are set:

- `TIBBER_ACCESS_TOKEN`: required for the live test
- `TIBBER_HOME_SELECTION`: optional, defaults to `first`
- `TIBBER_API_ENDPOINT`: optional, defaults to the configured Tibber endpoint

These variables are for local test execution only. They must not be committed and they do not replace the production rule that access data is stored in the database.

## Error Handling

- Missing access token fails explicitly.
- GraphQL errors fail explicitly.
- Missing price data fails explicitly.
- No silent fallback is allowed.
