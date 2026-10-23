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

## Leitstand View

The themes `leitstandLight` and `leitstandDark` render `LeitstandDashboard.vue` instead of the other dashboard layouts. The view has four zones: situation sentence, live flow of both storage systems (Victron and E3/DC) with live checks, 24 h plan with segments and reasons, and a measured review with quality figures.

It reads `/api/dashboard/summary` (every 15 s) and `/api/dashboard/review?hours=12|24|48` (every 5 min). Categories, checks, plan segments and review energies are calculated in `DashboardSummaryService`; the frontend only formats them as German text and charts. All power values are measured samples, never requested decision targets. The review relies on E3/DC battery and PV values stored with every live sample.

The review also contains a strategy comparison (`StrategyComparisonCalculator`). It replays the measured period with a simulated Victron battery that follows the simple rule "battery first": cover every import down to the planning minimum, store every surplus up to the planning maximum, never charge from the grid, never hold. Like the real controller, it ignores deviations inside the grid power deadband and keeps the deadband as import margin while discharging. Without that margin the baseline would win only because it regulates perfectly to 0 W. It compares that result with the real energy cost and with no Victron battery at all. The demand without the battery is `grid power - battery AC power`. Import is priced at the slot price: Tibber for today, the decision log for older slots. Export is priced at the feed-in compensation. If the real operation ends with more stored energy than the baseline, the difference counts at the average price of the period after discharge losses, so energy bought for the next day is not a loss. The replay assumes the E3/DC behaves the same as it really did, because its reaction to a different Victron behaviour cannot be replayed.

## Warmwasser im Leitstand

Leitstand Hell und Dunkel zeigen Warmwasser direkt auf dem Dashboard, zwischen
Live-Energiefluss und Batterieplanung. Eine gemeinsame Komponente verwendet die
Leitstand-Farbvariablen und aktualisiert `/api/weishaupt/hot-water` sowie die
frontend-sicheren Aktivierungseinstellungen alle 60 Sekunden unabhaengig von den
Batteriedaten. Sichtbar sind Isttemperatur, Messzeit und Datenalter, letzter
gemeldeter Betriebsstatus, Automatik/Anhebung, Preisfenster und Bedarfszeit,
Temperaturverlauf und die Auswertung der letzten Vorladung. Messluecken ueber
15 Minuten werden im Verlauf unterbrochen. Veraltete Messwerte (ueber 10 Minuten)
und Abruffehler bleiben deutlich erkennbar; fehlende Daten werden nicht als 0 gewertet.
Preisvorteile sind Modellwerte, keine gemessene Euro-Ersparnis. Eine Sollwertanhebung
wird nicht als laufender Warmwasser-Heizbetrieb dargestellt. Die anderen
Dashboard-Layouts sind nicht Bestandteil dieser Erweiterung.

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


### Lesbarkeit des 48-Stunden-Plans

Der Leitstand zeigt den Plan über die gesamte Inhaltsbreite mit vergrößerten Diagrammbereichen und Beschriftungen. Uhrzeit und Datum stehen getrennt an der Zeitachse. Die nächsten Schritte folgen darunter, auf breiten Bildschirmen zweispaltig. Auf schmalen Bildschirmen bleibt das Diagramm horizontal scrollbar, damit Viertelstunden-Balken und Beschriftungen lesbar bleiben; die Seite selbst darf dadurch keinen horizontalen Überlauf erhalten. Die Darstellung gilt für den hellen und dunklen Leitstand sowie eingebettete Leitstand-Ansichten.
