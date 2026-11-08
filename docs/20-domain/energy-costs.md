# Electricity costs and consumption

Operator request 2026-11-08: a page "Stromkosten & Verbrauch" that shows how much electricity the house uses, what is
really paid for grid power and how both develop. This document describes the calculation; it is extended with every
delivery step.

## Measured quarter hours (step 1)

Every UTC quarter hour is stored permanently in `EnergyQuarterHours`, so evaluations survive the deletion of raw data
(live samples are kept 60 days since 2026-11-08, before that 14 days).

| Group | Values | Source |
|---|---|---|
| Grid | import, export (kWh) | live samples (every second, since 2026-10-25) or decision log (about every 11 s, since 2026-05-05), whichever covers more of the quarter hour |
| Balance | house consumption, PV, Victron and E3/DC battery charge/discharge (kWh) | live samples only |
| Wallbox | car charging (kWh, part of the house consumption) | live samples only |
| Price | retail work price valid in the quarter hour (EUR/kWh) | decision log (`TibberPricePerKwh`, the price of the selected supplier at that time) |

- **Integration:** a measured value holds until the next one, at most 10 s (live samples) or 2 minutes (decision
  log). Longer gaps are not interpolated; each group stores its coverage in seconds (0 to 900).
- **House consumption** is the complete balance `grid + PV - Victron battery - E3/DC battery` (batteries positive =
  charging, grid positive = import), only for moments where all four are measured. All PV of the site is at the E3/DC
  (internal and external inverter); battery power is DC without loss correction (a few percent). The value stored
  in `LiveConsumptionSamples.HouseConsumptionWatts` is not used: it is made for the consumption forecast and leaves the
  E3/DC battery effect in on purpose.
- **Import cost** = measured grid import × valid price of the quarter hour; negative prices give negative cost. Grid
  charging of the batteries is paid at import time; their later discharge costs nothing again. Without a price the cost
  stays unknown (never 0).
- **Recalculation** is idempotent and keeps per group the more complete value: a recalculation from partly deleted raw
  data never replaces better stored values.
- **Background job** (`EnergyHistoryBackgroundService`): once a backfill from the oldest decision log entry, one UTC
  day at a time with 5 s pauses (progress in `energyHistory.backfilledThroughUtc`), then every 15 minutes the last three
  hours and once per day the previous day.

## Tariff periods (step 2)

`electricityTariff.periods` (JSON list, edited on the page, admins only): valid from a local day until the next
period, supplier, monthly base fee (everything billed per month: supplier, grid, metering) and feed-in compensation
per kWh. A missing value means unknown and is shown as "nicht hinterlegt", never as 0 EUR; nothing is applied silently
to earlier periods.

- **Base fee:** the monthly fee spread evenly over the days of that month, and per day over its real length
  (23 or 25 hours on daylight saving days); a running period gets the elapsed share.
- **Feed-in:** measured export x the compensation of the period valid on the local day of the quarter hour.
- **Total after compensation** = import cost + base fee - feed-in compensation; complete only when all parts are known.

## Periods, key figures and comparison (step 3)

`EnergyCostReportBuilder` (Business) builds the report; the endpoints only map DTOs.

- **Periods:** day, week (Monday to Sunday), month, quarter, year and free (at most three years), in local calendar
  days of Europe/Berlin; UTC bounds via the time zone, so DST days have 23 or 25 hours.
- **Coverage:** shares of the evaluated time with measured grid (and balance). A day or window is "measured" from 95 %.
  Import cost is complete only with sufficient grid coverage and a price for every imported kWh, otherwise it is
  marked as partial cost.
- **Daily averages** use only whole days: house consumption over days with 95 % balance coverage, import energy over
  days with 95 % grid coverage, import cost over days with complete cost. Excluded days are listed with the reason.
- **Weighted purchase price** = import cost / priced import energy.
- **Comparison** with the previous period of the same kind; for a running period the same elapsed part (same local
  wall-clock offset from the start, e.g. the 1st to the 8th, 16:00). A change is only shown when both sides are
  sufficiently measured.
- **Buckets:** time series per quarter hour (one day), hour (up to a week), day (up to 400 days) or month; cost bars
  and table per hour (one day), day (up to 62 days), month (up to two years) or quarter.
- **CSV:** the table rows plus a sum row, semicolon separated with decimal comma, units in the header, empty cells for
  unknown values, coverage per row.
- **API:** `GET /api/energy-costs/report?kind=day|week|month|quarter|year&date=YYYY-MM-DD` or
  `kind=custom&from=...&to=...`, `GET /api/energy-costs/report.csv` (same parameters), `GET /api/energy-costs/day?date=...`
  (quarter hours of one day), `GET/PUT /api/energy-costs/tariffs` (PUT admins only).
