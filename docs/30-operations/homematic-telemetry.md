# Homematic Telemetry

## Purpose

The controller records Homematic room climate values (temperatures, set points, valve openings, humidity) from the OpenCCU. The data is later combined with Weishaupt WEM portal values to tune heating curves and minimum flow temperatures based on measurements.

The data has no influence on battery decisions.

## Read-Only Rule

The integration must never change anything on the CCU. Only the XML-API list endpoints are called:

- `GET {baseUrl}/config/xmlapi/statelist.cgi?sid={token}`
- `GET {baseUrl}/config/xmlapi/devicelist.cgi?sid={token}`

No state-changing endpoint (for example `statechange.cgi`, `runprogram.cgi`) may be used.

## Access Data

The XML-API requires a token created in the CCU via `tokenregister.cgi`. Without it the API answers HTTP 200 with `<not_authenticated/>`, which is treated as an error.

- `homematic.apiToken` is a sensitive setting; frontend read APIs only expose whether it is configured.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `homematic.enabled` | `false` | No HTTP request is sent while disabled. |
| `homematic.baseUrl` | `http://homematic-raspi` | OpenCCU address. |
| `homematic.apiToken` | `null` | XML-API token (sensitive). |
| `homematic.pollingIntervalSeconds` | `300` | Minimum 60 seconds. |
| `homematic.sampleRetentionDays` | `400` | Allows comparing with the previous heating season. |

## Device Selection

Selection uses an allow-list of device types, because generic datapoint names are ambiguous: switch actuators report `ACTUAL_TEMPERATURE` as device temperature on channel 0, blinds and dimmers report `LEVEL` as position.

| Device type | Kind | Valve opening source |
|---|---|---|
| `HmIP-HEATING` (virtual group) | `HeatingGroup` | `LEVEL` × 100 |
| `HmIP-eTRV*` | `RadiatorThermostat` | `LEVEL` × 100 |
| `HM-CC-RT-DN` | `RadiatorThermostat` | `VALVE_STATE` (percent) |
| `HmIP-WTH*`, `HmIP-BWTH*`, `HM-TC-IT-WM-W-EU` | `WallThermostat` | – |
| `HmIP-FAL*` (underfloor actuator) | `UnderfloorHeatingZone` | `LEVEL` × 100 |
| `HmIP-SWO*` | `WeatherStation` | – |

Rules:

- Channel 0 (maintenance channel) is never used as a measurement channel.
- Thermostats, groups and weather stations need a channel with `ACTUAL_TEMPERATURE`; underfloor zones need a non-empty `LEVEL` (unused actuator outputs are skipped).
- On HmIP devices `VALVE_STATE` is a status code, not a percentage, and must not be used as valve opening.
- BidCos devices use `SET_TEMPERATURE`, `ACTUAL_HUMIDITY` and `CONTROL_MODE` instead of `SET_POINT_TEMPERATURE`, `HUMIDITY` and `SET_POINT_MODE`.
- Reachability comes from the `unreach` attribute of the device.
- Device types that report climate values but are not on the list are logged once per process start and not stored.

## Time Handling

`PolledAtUtc` is the time axis. The CCU datapoint timestamp only changes when the value changes and is stored separately as `LastChangedAtUtc`.

## Encoding

The XML-API answers in ISO-8859-1. Responses are parsed from the stream so the XML declaration determines the encoding and umlauts in device names stay intact.
