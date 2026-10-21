# Auswaehlbare Strompreisquellen

Stand: 21.10.2026. Implementierung fuer Tibber und dynamicOctopus Deutschland.
Kein Octopus-Konto vorhanden: bisher nur simulierte HTTP-Antworten, kein echter
Login oder Preisabgleich. Octopus bleibt bis zur Inbetriebnahme unaktiviert.

## Konfiguration und GUI

- Einstellungen > Prognosen > Strompreise: `electricityPrices.provider`
  mit `tibber` (Standard, auch bei fehlendem Setting) oder `octopusGermany`.
- Tibber verwendet unveraendert seine bisherigen Zugangseinstellungen.
- Octopus benoetigt `octopus.apiKey` (sensitive, nicht in Frontend-Leseantworten)
  und `octopus.accountNumber` (Kundennummer A-...).
  Die normale Kundennummer-Einstellung nutzt intern den Platzhalter
  `not-configured`; die GUI zeigt dafuer ein leeres Eingabefeld. Der Adapter
  weist den Platzhalter ohne API-Aufruf zurueck.
- Zugangsdaten koennen vor dem Wechsel gespeichert werden. Die GUI speichert
  eine geaenderte Anbieterauswahl zuletzt. Nicht ohne aktiven Vertrag umschalten.
- Leitstand hell/dunkel zeigt die ausgewaehlte Strompreisquelle im Systemstatus.
  Das ist eine Konfigurationsanzeige, kein erfolgreicher Verbindungstest.
- `electricityPrices.providerChangedAtUtc` wird automatisch bei tatsaechlichem
  Anbieterwechsel gesetzt und kann nicht direkt ueber den Settings-Service
  bearbeitet werden. Wiederholtes Speichern derselben Auswahl veraendert ihn nicht.
  Der Anfangswert `never` bedeutet: noch kein Anbieterwechsel, keine Begrenzung
  der bisherigen Ersparnisrechnung.

## Octopus-API

Fest vorgegebener deutscher HTTPS-Endpunkt:
`https://api.oeg-kraken.energy/v1/graphql/`.
Kein Versand von API-Keys an frei konfigurierbare URLs oder die britische API.
Anmeldung per `obtainKrakenToken(input: { APIKey: ... })`; Token als
`Authorization` ohne automatisch ergaenztes Bearer-Praefix.
E-Mail/Passwort-Anmeldung ist nicht implementiert.

Preisabfrage: `account.properties.electricityMalos.agreements.unitRateForecast`
mit `validFrom`, `validTo` und
`TimeOfUseProductUnitRateInformation.rates.latestGrossUnitRateCentsPerKwh`.
Dezimale Brutto-ct/kWh werden durch 100 zu EUR/kWh; auch Null und negative Preise.
Keine erneute Addition von Mehrwertsteuer/Netzentgelten und keine Verteilung des
monatlichen Grundpreises auf Entscheidungs-Slots.

Die erste Version akzeptiert genau eine Marktlokation mit Preisprognosen und
genau einen Preis pro Prognosezeitraum. Mehrdeutige Preiszonen, mehrere
Marktlokationen, ueberlappende Vertraege/Slots oder unbekannte Formate schlagen
sichtbar fehl. Keine Auswahl per Listenposition. Eine explizite Mehrzaehler-
Auswahl kann erst mit einem realen Konto und bestaetigten IDs erweitert werden.
Intervalle muessen 15 Minuten lang und UTC-ausgerichtet sein; Offset-Zeitstempel
werden nach UTC normalisiert. Keine Annahme von immer 96 Slots pro lokalem Tag.
Fehlende Intervalle werden nicht mit Null oder Fremdanbieterpreisen aufgefuellt.

Token-Erneuerung nach 45 Minuten; Preisabruf alle 10 Minuten. Prozesslokaler,
serialisierter Cache mit Credential-/Kontofingerprint; kein Rohschluessel als
Cache-Key. Fehler sperren erneute API-Versuche eine Minute. Es gibt keinen
Still-Fallback auf alte Octopus-Daten oder Tibber-Preise. HTTP 401/403, GraphQL-
und Schemafehler werden ohne fremde Fehlermeldungen/Antworttexte gemeldet,
damit Zugangsdaten nicht in Logs gelangen. Nach Fehler wird das Token verworfen.
HTTP-Timeout 20 Sekunden, kuerzere Aufrufer-Cancellation bleibt wirksam.

## Integration und Historie

`IElectricityPriceForecastProvider` ist die gemeinsame Business-Schnittstelle.
Die zentrale scoped Auswahl wird von Batterieentscheidungen, Batterieprognose,
Dashboard, Ersparnisrechnung und Warmwasserautomatik verwendet.
`ITibberPriceForecastProvider` bleibt als Kompatibilitaetsalias fuer den
Tibber-Adapter bestehen. Bestehende DTO-/Datenbankfelder und Slot-Typen mit
`Tibber` im Namen bleiben aus Kompatibilitaetsgruenden erhalten; sie tragen
kuenftig den tatsaechlich ausgewaehlten Arbeitspreis.
Neue Entscheidungs-Eingabeprotokolle enthalten `ElectricityPriceProvider`.

Octopus veroeffentlicht Morgenpreise laut Anbieter erst am spaeten Nachmittag.
Die Prognose hat keine feste 13-Uhr-Freigabe. Vergleichstags-Schaetzungen duerfen
veroeffentlichte Preise desselben Anbieters nutzen; Octopus verwendet nicht
die alten Tibber-Entscheidungslogs. Historische Octopus-Preise werden beim
Rueckwechsel auch nicht als Tibber-Schaetzung verwendet.
Netzladen bleibt auf veroeffentlichte, nicht geschaetzte Preise beschraenkt.

Tagesbasierte Ersparnis-Summen koennen einen Anbieterwechsel innerhalb eines
Tages noch nicht korrekt trennen. Deshalb bleiben bisherige und Wechsel-
Tageswerte unveraendert; fuer diesen Tag wird keine vollstaendige Ersparnis
behauptet. Erst ab dem folgenden lokalen Kalendertag (Europe/Berlin) werden
Summen mit dem neuen Tarif neu berechnet. Ein Rueckwechsel setzt diese Grenze
erneut. Alte gespeicherte Tageswerte werden nicht geloescht oder umbewertet.

## Spaetere Inbetriebnahme

1. API-Key und Kundennummer lokal in den Einstellungen speichern, nicht im Chat.
2. Aktiven dynamicOctopus-Vertrag sowie API-Key-Berechtigung bestaetigen.
3. Bei Umschaltung wenigstens aktuellen und negativen/positiven Beispielpreis
   gegen die Octopus-App vergleichen; Faktor 100 und Bruttoanteile pruefen.
4. Heute/Morgen-Abdeckung, Zeitumstellung und Verhalten bei Ausfall beobachten.
5. Erst danach die live gepruefte Integration als betriebsbereit betrachten.

Quellen:

- [Deutsche Octopus-Preisanleitung](https://octopusenergy.de/wissen/alles-zu-deinem-vertrag/dynamisch-sparen-per-api)
- [Authentifizierung und APIKey](https://developer.oeg-kraken.energy/graphql/reference/input-objects/obtainjsonwebtokeninput/)
- [Brutto-Preiseinheit](https://developer.oeg-kraken.energy/graphql/reference/objects/timeofusetimeslotunitrate/)

Die einfache Preisanleitung beschreibt noch E-Mail/Passwort. Die aktuelle
Referenz markiert diese Felder als deprecated; fuer die Implementierung gilt
deshalb der dokumentierte APIKey-Weg. Keine Live-Verifikation ohne Kundenkonto.
