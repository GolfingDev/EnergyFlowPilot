# Auswaehlbare Strompreisquellen

Stand: 25.10.2026. Preisquellen: Tibber, dynamicOctopus Deutschland und Energy-Charts mit eigenem Tarifaufschlag.
In dieser Cloud-Pruefung waren keine Octopus-Zugangsdaten verfuegbar: simulierte HTTP-Antworten, kein echter
Login oder Preisabgleich. Bestehende Installationen werden nicht automatisch auf eine andere Quelle umgeschaltet.

## Konfiguration und GUI

- Einstellungen > Prognosen > Strompreise: `electricityPrices.provider`
  mit `tibber` (Standard, auch bei fehlendem Setting), `octopusGermany` oder `energyCharts`.
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


## Beliebiger Anbieter mit Energy-Charts

`electricityPrices.supplierName` ist ein frei wählbarer Anzeigename, unabhängig vom technischen Anschluss. Eine beliebige Anbieterschnittstelle lässt sich dadurch nicht automatisch anbinden: derzeit sind Tibber und Octopus direkt implementiert. Für andere börsengekoppelte Tarife kann die Preisquelle `energyCharts` verwendet werden.

- Öffentlicher, fest vorgegebener Endpunkt: `https://api.energy-charts.info/price`, Parameter `bzn`, `start`, `end`; kein API-Schlüssel. Gebotszone `energyCharts.biddingZone`, Standard `DE-LU`.
- Antwort: `unix_seconds`, `price`, `unit` (`EUR / MWh`). Nullwerte bleiben fehlende Preise. 15-, 30- und 60-Minuten-Intervalle werden nach UTC-Viertelstunden abgebildet; eine Stunde erhält vier identische veröffentlichte Preise. Doppelte/ungeordnete Zeitpunkte, unbekannte Einheiten und nicht unterstützte Lücken werden abgewiesen. Negative Preise bleiben möglich.
- Konfigurierbarer Bruttoaufschlag `energyCharts.grossSurchargePerKwh` (API/Persistenz EUR/kWh, GUI ct/kWh), einschließlich Netzentgelten, Abgaben, Lieferantenmarge und deren Mehrwertsteuer. Der Börsenanteil wird separat mit `energyCharts.vatPercent` (Standard 19 %) versteuert.
- Formel: `Bruttoarbeitspreis = Börsenpreis_EUR/MWh / 1000 × (1 + MwSt./100) + Bruttoaufschlag_EUR/kWh`. Beispiel: 100 EUR/MWh, 19 % MwSt., 15 ct/kWh Bruttoaufschlag ergeben 26,9 ct/kWh. Der Aufschlag wird nicht erneut versteuert. Ein monatlicher Grundpreis gehört nicht in die wirtschaftliche Bewertung einer zusätzlichen kWh; er wird im Tarifvergleich berücksichtigt.
- Der Aufschlag ist anfangs ausdrücklich **nicht eingerichtet**, nicht null Euro. Die Oberfläche zeigt den internen Marker `not-configured` als leeres Feld; der Adapter verweigert den Abruf, bis eine Zahl einschließlich expliziter 0 eingetragen wurde. Der Anbietername ist optional und wird bei fehlendem Wert nicht im Dashboard eingeblendet.
- Cache: 30 Minuten, nur rohe Börsenpreise, nach Gebotszone und Datum. Neue Aufschläge/Steuersätze werden auch auf gecachte Marktpreise neu angewendet. Fehler führen zu einer Minute Wiederholsperre; kein stiller Ersatz durch kostenlose, alte oder andere Anbieterpreise.
- Datenquelle/Attribution: Energy-Charts, Fraunhofer ISE, `https://api.energy-charts.info/`. Die Preisübersicht zeigt Quelle und selbst eingetragenen Anbieter. Quellenbeleg für Format/Einheit: EVCC-Template `https://github.com/evcc-io/evcc/blob/master/templates/definition/tariff/energy-charts-api.yaml`.
- Der Liveabruf war in dieser Cloud-Umgebung durch Proxy/DNS gesperrt. Format, Umrechnung, Ausfälle, negative Preise und Zeitumstellung wurden mit simulierten Antworten geprüft; ein erfolgreicher Liveabgleich auf der Anlage steht aus.

Für die Restenergiebewertung kann der generische Adapter historische Börsenpreise mit den **heutigen** Tarifparametern umrechnen. Dies sind rekonstruierte Preise dieses Tarifmodells, keine tatsächlichen historischen Rechnungsbeträge. Eine Änderung der aktiven generischen Konditionen setzt den bestehenden Wechselzeitpunkt neu; das Vorbereiten inaktiver Konditionen beeinflusst die aktuelle Quelle nicht. Direkte Anbieterhistorien werden ab einem tatsächlichen Quellenwechsel getrennt gehalten.

## Vergleich dynamischer 15-Minuten-Tarife

Unter „Stromtarif & Prognosen“ gibt es einen ausklappbaren Tarifvergleich. Anbieter können frei ergänzt werden. Namensvorlagen für Tibber, Octopus, aWATTar, Ostrom und Rabot enthalten absichtlich keine ungeprüften Preiswerte und behaupten keine bestätigte 15-Minuten-Abrechnung. Es gibt keinen vollständigen automatisch gepflegten Markt- oder Regionalkatalog. Für eine belastbare Vorbelegung aller Anbieter fehlen verifizierte aktuelle Konditionen und gegebenenfalls Postleitzahl/Verbrauch; solche Werte dürfen nicht erfunden werden.

Angebote werden unter `electricityPrices.comparisonOffers` als JSON gespeichert: Name, Bruttoaufschlag EUR/kWh, Grundpreis brutto EUR/Monat, Umsatzsteuer auf den Börsenanteil, bestätigtes Abrechnungsintervall, Quelle/Preisblatt, Preisstand und optional gültige PLZ. Die Oberfläche liegt in den weiterhin auf Administratoren beschränkten Einstellungen. Bearbeitung/Speichern bleibt auf Administratoren beschränkt; der reine GET-Vergleichsendpunkt ist als Lesezugriff auch für authentifizierte Betrachter zugänglich. Eine Vergleichsabfrage ändert weder den aktiven Anbieter noch dessen Preiseinstellungen.

`GET /api/electricity-prices/compare?annualConsumptionKwh=4000&postalCode=12345&days=14` nutzt für alle Angebote dieselbe vollständige Marktpreisreihe der letzten abgeschlossenen 7, 14 oder 30 Tage (API 1–30), begrenzt nach Europe/Berlin einschließlich Zeitumstellung. Nur Angebote mit explizit 15 Minuten, vollständigen Preisen, Quelle und Preisstand sowie passender PLZ werden bewertet. Andere Angebote werden mit Ausschlussgrund aufgeführt. Es wird nicht automatisch bestätigt, ob das Preisblatt eines Anbieters aktuell oder korrekt übertragen ist.

Das Modell unterstützt Börsenpreis plus festen Bruttoaufschlag und monatlichen Grundpreis. Die Verbrauchsverteilung ist gleichmäßig, ohne individuelle Last-/Ladestrategie, Boni, Preisgarantien oder komplexe Preisdeckel. Die Jahressumme ist `Jahresverbrauch × mittlerer Bruttoarbeitspreis + 12 × Monatsgrundpreis`. Sie ist eine klar bezeichnete Hochrechnung eines kurzen historischen Preiszeitraums, keine garantierte Jahresrechnung oder Wechsel-Empfehlung. Sortiert wird nach dieser Modell-Jahressumme, nicht allein nach dem Aufschlag.
