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
