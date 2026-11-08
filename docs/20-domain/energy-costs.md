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

## Page (step 4)

Route `/energy-costs` ("Stromkosten" in every navigation: Zentrale sidebar and phone bar, classic top navigation,
executive sidebar and the phone navigation row). The page uses the shared Zentrale building blocks and the colours of
the selected design; charts use chart.js.

- **Period bar:** day, week, month, quarter, year, free; previous/next, "Aktuell". The selection lives in the URL
  (`?kind=month&date=...`), so a view can be shared.
- **Key figures:** house consumption (with daily average), grid import (with export), import cost (with weighted
  purchase price and cost per day, "Teilkosten" when incomplete), total after compensation (base fee and feed-in, or
  "nicht hinterlegt"); each with the change against the previous period.
- **Charts** (one y-axis each, fixed colours per quantity, validated palette for light and dark designs):
  consumption and supply (house, grid import, PV; both batteries on demand as net charging), cost bars (import cost,
  base fee, feed-in compensation negative; a day bar opens the day detail), comparison (energy and cost, current
  against previous or the same elapsed part), day detail (import, retail price and import cost per quarter hour as
  three aligned charts instead of a dual axis).
- **Collapsed details:** table with all columns, coverage and CSV download; excluded days with reason; tariff periods
  (admins edit); a short calculation note.
- **Navigation fixes found while checking the page:** the classic phone navigation row stacks icon and label below
  520 px, the classic top navigation drops its subtitles below 1700 px and its tagline below 1500 px and hands over to
  the navigation row below 1160 px, the Zentrale phone bar shrinks its labels below 420 px; otherwise five or six
  entries overlapped or widened the page.

## Available history (2026-11-08)

| Quantity | From | Source |
|---|---|---|
| Grid import/export, import cost | 2026-05-05 | decision log (coverage above 98 % per month) |
| House consumption, PV, batteries, car | 2026-10-25 | live samples |

Monthly import cost computed from the decision log for the operator on 2026-11-08 (without base fee): May (from the
5th) 249.1 kWh / 68.30 EUR (13.1 kWh without price), June 45.0 kWh / 10.99 EUR, July 79.4 kWh / 22.87 EUR, August
65.8 kWh / 19.49 EUR, September 93.3 kWh / 29.30 EUR, October 747.8 kWh / 222.45 EUR, November to the 8th 370.9 kWh /
157.73 EUR; check 1 to 7 November: decision log 326.1 kWh against live samples 324.2 kWh (0.6 %). The live samples
were also exported to `backups/LiveConsumptionSamples-20261108-1622.csv.gz` on the Pi before the retention was raised.

## Limitations

- Battery power is measured on the DC side without loss correction; the house balance can be a few percent off.
- The E3/DC values (PV, battery) come from the Hager API about every 30 s; within that time the balance holds the
  last value.
- All PV is assumed behind the E3/DC (internal and external inverter); the Victron has no PV of its own.
- The price is the work price the decision log recorded for the quarter hour (the selected supplier at that time);
  where the controller did not run, there is no price and the cost is partial. Tibber's base fee has no history in the
  controller; it has to be entered as a tariff period.
- Octopus (from 2026-11-12) prices on the intraday auction IDA1; the stored price is Octopus' own, so costs stay
  correct.

## Checks

- Unit tests (Business): integration and quarter-hour split, gaps without interpolation, house balance with all flows
  and only when complete, negative and missing prices, per-group merge (no downgrade) and idempotent recalculation,
  calendar periods with Monday weeks, month and quarter boundaries, DST days (23/25 hours, base fee share), same
  elapsed part across summer/winter time, tariff change within a period, partial cost and excluded days, comparison
  only when both sides are measured, CSV format, tariff validation.
- Page checked in headless Chrome with synthetic data in the Zentrale (dark and light) and classic designs at
  1440, 1280 and 1100 px and at 390 px phone width (DevTools device emulation): no horizontal overflow, aligned day
  charts, readable navigation.
