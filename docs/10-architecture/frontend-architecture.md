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


### Build-Kennung im Kopfbereich

Alle Designs zeigen im globalen Kopfbereich eine kompakte Build-Information: kurze Git-Commit-Kennung und Erstellungszeitpunkt des Frontend-Artefakts in Europe/Berlin. Im normalen Kopf steht sie unter dem Produktnamen, bei den Layouts mit Seitenleiste unter dem Seitentitel. Der Tooltip enthält die volle Commit-Kennung, den Commit-Zeitpunkt und die Build-Zeit. Das Datum ist die Erstellungszeit des Frontends, keine behauptete Veröffentlichungs- oder Serverstartzeit.

Vite bettet die Metadaten automatisch beim Build ein; die vorhandenen Installations- und Update-Skripte verwenden bereits diesen Build. Ein Browser-Reload verändert diese Angaben nicht. Entwicklungsbetrieb wird ausdrücklich als „Entwicklung“ markiert, lokale Änderungen mit einem Stern. Ohne Git-Metadaten erscheint „unbekannt“. Die Kennung beschreibt das geladene Frontend-Artefakt, nicht eine separat geprüfte Backend-Version.


### Zeitlicher Zoom im Plan

Der Leitstand bietet Zeitspannen von 1, 3, 6, 12, 24 und 48 Stunden. Pfeile verschieben den sichtbaren Beginn um jeweils eine Stunde, ein zugänglicher Schieberegler um einzelne Viertelstunden. „Zum Planbeginn“ setzt den Ausschnitt zurück; 48 Stunden stellt die Gesamtansicht wieder her. Die Anzeige nennt die tatsächlichen Zeitgrenzen.

Der Zoom filtert vorhandene Plan-Slots ohne neue Prognoseabfrage und ohne Änderungen an der Steuerung. Tooltip, Preisstatus und Kurven verwenden denselben Ausschnitt; Zeitachsenbeschriftungen passen sich der Zeitspanne an. Die Jetzt-Markierung erscheint nur, wenn der aktuelle Zeitpunkt im Ausschnitt liegt. Bei Live-Aktualisierungen bleibt ein ausgewählter Zeitpunkt verankert, soweit er noch im verfügbaren Plan liegt; die Auswahl wird an den Planrändern begrenzt.


### Ruhige Standardansicht nach Einführung der gemeinsamen Steuerung

Die templateübergreifende Akkuübersicht zeigt im Normalfall nur Betriebsstatus, Datenstand sowie Ladestand und gemessene Leistung beider Speicher. Sollwerte, Festlegung des Slots, Kapazitäten, Reserven, Modellannahmen und Tabellen liegen unter „Status & Planungsdetails“. Das Formular für eine neue manuelle Ladung ist separat eingeklappt. Dadurch bleibt der Einstieg in alle fünf Designs kompakt.

Eine aktive manuelle Ladung samt Fortschritt bleibt sichtbar; der Stop bleibt für Administratoren unmittelbar erreichbar. Betrachter sehen den aktiven Zustand ohne Bedienrechte. Störungs-/Übergangsmeldungen und die bestehende Warnung zur ausstehenden Rücknahme werden außerhalb der technischen Details angezeigt. Die bisherige Bestätigungsabfrage für manuelle Rücknahme bleibt erhalten.

Im Leitstand sind ausführliche Entscheidungsbegründungen, nächste Schritte und die Tabelle der letzten Entscheidungen zunächst geschlossen. Messfluss, Prüfhinweise und der große zeitlich konfigurierbare Plan bleiben sichtbar. Diese Änderung betrifft Darstellung und Informationshierarchie; sie ändert keine Ladeentscheidung oder Zugriffsberechtigung.


### Einheitliche Entscheidungsübersicht: Jetzt – Warum – Als Nächstes

Alle fünf Designs zeigen einen gemeinsamen kompakten Baustein mit aktueller Entscheidung, aktuellem Strompreis und unmittelbar lesbarer Begründung. Daneben steht der nächste abweichende Zustand im verfügbaren Plan mit Zeitpunkt, Preisstatus und eigener Begründung. Echte Warnungen und kritische Beobachtungen bleiben sichtbar; wiederholte positive Regelprüfungen und bloße Info-Checklisten entfallen. Die feste Slot-Zusage wird nicht mehr im Dashboard erläutert. Zeitfenster, Zoom und Leistungs-/Ladestandsmessungen bleiben verfügbar.

Für die gemeinsame Steuerung liefert die API eine gesonderte Anzeigeprojektion `overview`. Sie berücksichtigt freigegebene Ziele beider Akkus: Ein stehender Victron darf die Entladung des E3/DC nicht als gemeinsame Ruhe ausgeben. Eine geplante Ladung mit aktuell null freigegebener Leistung wird als Pause angezeigt; abgelaufene Ausführungsstände kündigen keinen aktiven oder nächsten Vorgang an. Manuelle Ladung überschreibt keine Prognose durch ein scheinbares PV-Laden und unterdrückt die Ankündigung einer automatischen Folgeaktion. Die bisherigen Victron-Kommandos, Messwerte und Abrechnungsfelder werden dabei nicht verändert.

Die Begründung kommt aus dem fachlichen Planer, nicht aus im Frontend nachgebauten Optimierungsregeln. Sie nennt aktuelle Preise, geplante spätere Verbrauchspreise, PV-Überschuss, Reserve oder konkreten Folgebedarf statt allgemeiner Listen zu Reserven und Leistungsgrenzen. Historische Preisannahmen sind ausdrücklich als historisches Niveau bezeichnet. Fehlt ein Log-Preis, darf ein vorhandener veröffentlichter Preis des aktuellen Forecast-Slots die Anzeige ergänzen; ein geschätzter Slot wird nicht als aktueller Preis ausgegeben.

### Integrierte Speicheranzeige und Horizont

Die fünf bestehenden Designs zeigen beide Akkus als Symbolpaar direkt im gemeinsamen Zustandskasten. Die Bedienung und eingeklappten Planungsdetails liegen im selben Kasten; der zusätzliche obere Block entfällt. Ein UI-Kontext verbindet die eingebettete Anzeige mit den bisherigen API-Aufrufen der Dashboard-Seite. Berechtigungen, Rücknahmebestätigung und unmittelbar erreichbarer Stop einer aktiven manuellen Ladung bleiben erhalten. Fehlende Messwerte erscheinen als unbekannt.

Das sechste Design „Horizont“ setzt auf eine große interaktive Zeitlinie in Nachtblau, Koralle und Türkis. Aktueller Preis, Entscheidung und Begründung stehen neben dem Plan. Victron und E3/DC erhalten getrennte Ladestandskurven; fehlende Prognosewerte bleiben Lücken. Preisbalken unterstützen negative und geschätzte Preise. Maus und Pfeiltasten wählen einzelne Viertelstunden; Zeitfenster von 6, 12, 24 und 48 Stunden nennen ihren tatsächlichen Zeitraum. Weitere Analysen bleiben eingeklappt erreichbar. Die bisherige Standardauswahl bleibt bestehen. Diese Änderungen betreffen ausschließlich Darstellung und Bedienung, nicht die Optimierung oder Speichersteuerung.

### Netzkosten im gewählten Analysezeitraum

Energiezentrale und Kachelwerk zeigen direkt an ihren Netzenergie-Graphen die Netzkostensumme für denselben gewählten Abschnitt (24 Stunden, 48 Stunden oder 7 Tage). Die Summe stammt aus `DashboardReview.costs.actualCost`: erfasste Bezugsenergie bewertet mit den jeweiligen Intervallpreisen, abzüglich Einspeisevergütung, ohne Grundpreis. Sie wird nicht aus Gesamtenergie und Durchschnittspreis im Frontend nachgerechnet. Auch negative Summen bleiben sichtbar.

Beim Zeitraumwechsel werden alte Beträge ausgeblendet und der neue Datenstand abgewartet. Fehlende Kosten erscheinen als unbekannt, nicht als null. Bei unvollständiger Preisbewertung nennt die Anzeige den Anteil bewerteter erfasster Messintervalle und bezeichnet die Summe als unvollständig. Messlücken werden dadurch nicht als vollständig erfasster Verbrauch ausgegeben; die Darstellung ist eine Auswertung der erfassten Messwerte, keine Stromabrechnung.


## Energy Flow Zentrale

The designs `zentrale` (dark) and `zentraleLight` render their own app frame (`ZentraleShell.vue`) and dashboard (`ZentraleDashboard.vue`). `zentrale` is the default for browsers without a stored design; a stored choice stays. All other designs are unchanged.

### Navigation

Sidebar with Dashboard, Analyse, Steuerung, Automationen and Einstellungen (admins only). Tablets get an icon rail, phones a bottom bar. The car and heating pages stay reachable from Steuerung and the context panel. Below the navigation the user switches the operating level (Standard / Experte) and light/dark; other designs remain selectable.

### Dashboard

Reading order: key figures, energy flow with status and automations beside it, 24 h forecast with planned actions below. Every key figure, flow node, status sentence, automation and planned action opens the same context panel on the right (bottom sheet on phones). Control starts there: a click on "Akku 1" shows mode (Automatik / Sofort laden), charge limit and reserve, and power limits, without changing the page.

### Energy flow

The house is the busbar: each line connects exactly one component with the house. PV charging a battery therefore shows "PV → Haus" and "Haus → Akku", which is what the meters measure; no line claims an unmeasured allocation between two components. Lines animate only for measured power above 50 W and only while live data are at most two minutes old. Line direction follows the measured sign. The heat pump has no live power measurement; its line is a plain dotted connection. Components that are not set up (wallbox, heat pump, E3/DC) are not drawn. The house value is the measured balance (grid minus both batteries plus PV) and includes the car. On phones the drawing becomes a list with the same values and directions.

### Explanations

Status sentences combine measured facts with texts the backend delivers (planner reason, charging control reason, hot water plan). The frontend never invents a reason. Warnings and critical checks come first; normal operation is not shown as a warning. Automations show rule, state, current action, trigger text, affected devices and the next planned change.

### Control feedback

Setting changes show four states: changed here, sent, stored (the value read back from `/api/settings` matches), and taken over by the controller. The decision worker reads all settings at the start of each cycle; `/api/status` reports `decisionWorkerLastCompletedCycleStartedAtUtc`, the start of the last successfully completed cycle. A setting counts as taken over once that time is later than the confirmation and, with joint control active, the joint plan was also calculated afterwards. Until then the panel asks every 10 s for at most three minutes. Manual charging shows sent, accepted by the backend, reported active, and the battery measuring charging power. Errors and rejected commands are shown in German next to the control. Controls are only offered for what the backend supports: the E3/DC has no settable power limit and without joint control no command path at all; this is stated in the panel.

### Expert level

`expertMode` (`uiMode.ts`, stored in the browser) is one switch for all views and is the same as the extended mode of the settings page. It adds technical parameters (hard minimum, wear cost, horizon, hot water thresholds, rule IDs, execution details). Safety-relevant states (stale data, recovery pending, simulation mode, device errors) are visible on both levels.

### Settings

The settings page keeps its three zones and adds a search over all settings, including expert ones; a hit opens the section, enables the expert level if needed and highlights the field. Recently used and recently saved settings are offered as shortcuts.

### Data

One shared store (`useZentraleData.ts`) polls the summary, joint battery state and wallbox every 15 s, and car, charging control and plan, hot water, status and settings every 60 s, only while a Zentrale view is open. The classic dashboard requests are skipped while the Zentrale design is active. Colours of energy kinds are defined once as `--z-*` tokens in `styles.css` and used by flow, key figures and charts.
