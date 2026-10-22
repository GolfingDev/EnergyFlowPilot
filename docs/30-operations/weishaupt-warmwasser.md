# Weishaupt Biblock: Modbus-Warmwasserautomatik

Die Warmwasseroptimierung erfolgt auf Benutzerwunsch direkt über Modbus TCP.
Der bestehende FC04-Collector bleibt unabhängig und rein lesend.

Quelle: https://www.weishaupt.si/uploads/tx_weishaupt_documents/documents/83807301.pdf
(83807301, 1/2025-11, Seiten 3 und 10). Register 42103 ist „Warmwasser Normal“,
Einheit 0,1 °C; Lesen per FC03, Schreiben eines einzelnen Registers per FC06.
Die Protokolladresse ist 42103. Beim installierten WBB wurden am 19.10.2026
die vollstaendigen Adressen 30001, 33101, 32102 (FC04) und 42103 (FC03)
vom Controller-Host aus rein lesend bestaetigt. Offsets von 30001 bzw. 40001
werden mit Modbus-Ausnahme 02 abgewiesen und duerfen nicht verwendet werden.

## Verhalten

- `weishauptHotWater.enabled` ist nach Installation false; Modbus muss ebenfalls aktiviert sein.
- Die Preisplanung vergleicht zusammenhaengende Ladefenster vor der naechsten
  Bedarfszeit (Standard 18 Uhr Europe/Berlin) mit dem Nachheizen zur Bedarfszeit.
  Es werden nur veroeffentlichte EUR-Preise mit lueckenloser Abdeckung verwendet.
  Bei gleichen Kosten wird das spaetere Fenster bevorzugt. Die alte absolute
  Einstellung `cheapPricePerKwh` bleibt aus Kompatibilitaetsgruenden erhalten, wird aber nicht mehr ausgewertet.
- Ein geschaetzter Mehrverbrauch von 15 Prozent fuer hoehere Temperatur und
  Speicherung sowie mindestens 0,03 EUR/kWh Preisvorteil verhindern Vorladen
  bei geringem Preisabstand. Dies ist eine Modellannahme, keine gemessene Effizienz.
  Negative Preise werden konservativ ohne Verguetung zusaetzlicher Verluste bewertet.
- Alternativ loest mindestens 2000 W aktueller PV-Netzeinspeisung nach Abzug
  von Batterieentladung eine Anhebung aus. PV-Erzeugung allein genuegt nicht.
  Beim Halten genuegen 200 W; bei Preissteuerung gilt 0,02 EUR/kWh Hysterese.
  Veraltete (>120 Sekunden) oder fehlende Daten gelten für das jeweilige Signal als ungültig.
- Der gelesene normale Sollwert wird um 5 K angehoben, maximal auf 55 °C.
  Akzeptierter Ausgangswert: 35 bis 50 °C; Start bei 45 °C ergibt 50 °C.
- Nach Wegfall beider Signale wird nach mindestens 10 Minuten zum gespeicherten
  Ausgangswert zurueckgeschaltet. Ebenso endet die Anhebung, wenn der erhoehte
  Istwert erreicht oder das geplante Ladefenster beendet ist. Ein unabhaengiges
  Sicherheitslimit von vier Stunden begrenzt jede Anhebung;
  nach jeder Rücknahme gilt eine Pause von 30 Minuten. Prüfintervall: 60 Sekunden.
- Deaktivierung (auch des Collectors) veranlasst die Rücknahme einer eigenen Anhebung.
  Automatikzustand und Ausgangswert werden vor dem Schreiben in SQLite gespeichert,
  damit nach Neustart oder verlorener Schreibbestätigung eine Rücknahme möglich bleibt.
- Ein abweichender manuell gesetzter Sollwert wird respektiert und sperrt weitere
  automatische Anhebungen bis zum Deaktivieren und erneuten Aktivieren.
- Geänderte Zieladresse bei aktiver Anhebung blockiert weitere Aktionen: zuerst die
  ursprüngliche Verbindung wiederherstellen und dort die Anhebung zurücknehmen.
- Jede tatsächliche Änderung benötigt eine vorherige Ereignisprotokollierung und wird
  per FC03 zurückgelesen. Wiederholtes Schreiben unveränderter Werte entfällt wegen
  der EEPROM-Grenze. Unklare Schreibresultate werden im nächsten Zyklus abgeglichen.
- Ausschließlich 42103 wird geschrieben. Zeitprogramm, Absenksollwert, Heizkreise,
  Heizstabfreigaben und Warmwasser-Push werden nicht verändert. Außerhalb des
  Normal-Zeitprogramms erzwingt die Sollwerterhöhung daher keine Ladung.

Bei Netzwerk- oder Stromausfall kann der dauerhaft gespeicherte Sollwert erst nach
Wiederherstellung der Verbindung zurückgenommen werden; das Gerät bietet für 42103
keine dokumentierte automatische Ablaufzeit. Die lokale Regelung bleibt zuständig.

Preisvorteil, Mehrverbrauchsannahme, Bedarfsstunde, anfaengliche Ueberbrueckungsdauer,
Ladedauer und PV-Schwelle sind einstellbar. Temperaturgrenzen,
Mindestdauer, maximale Dauer und Pause sind feste Grenzen. Es erfolgt kein
automatisches Deployment und kein Hardware-Schreibtest im Rahmen der Unit-Tests.

## Speicher und Auswertung

### Interne Energiezaehler (Erweiterung vom 20.10.2026)

Der Collector liest zusaetzlich ausschliesslich per FC04 jeweils vier unsigned
16-Bit-Register in ganzen kWh (heute, gestern, laufender Monat, laufendes Jahr):

| Adressen (inklusive) | Bedeutung |
| --- | --- |
| 36101–36104 | Waerme gesamt |
| 36201–36204 | Heizungswaerme |
| 36301–36304 | Warmwasserwaerme |
| 36701–36704 | Elektrische Aufnahme der gesamten Waermepumpe |

361xx–363xx: Hersteller-Modbus-Dokument 83807301, 1/2025-11, Seite 15.
367xx steht dort nicht; Quelle ist die Implementierung unter
https://github.com/OStrama/weishaupt_modbus/blob/main/custom_components/weishaupt_modbus/hpconst.py
(PARAMS_ENERGY). Die vier 367xx-Register antworteten am installierten Geraet am
20.10.2026 mit 12, 13, 197 und 5723 kWh. Abgleich mit Regleranzeige/WEM steht
noch aus; dies ist kein geeichter separater Stromzaehler.

Explizite Modbus-Ausnahmen 01/02 bei optionalen Energie-Registern ergeben null;
andere Geraete-, Transport- und Protokollfehler bleiben Fehler des Pollings.
Ein gesamter Modbus-Abruf ist auf 30 Sekunden begrenzt.
65535 bleibt ein gueltiger unsigned Zaehlerstand, kein Temperatur-Fehlerwert.
SQLite erhaelt additive nullable Spalten; historische Werte werden nicht aufgefuellt.
Die API liefert den neuesten Telemetriestand als separates Energie-DTO, ohne
bei fehlenden Registern aeltere Zaehlerstaende unter neuem Zeitstempel anzuzeigen.
Leitstand hell/dunkel zeigt die vier Perioden mit Messzeitpunkt und
Veraltet-Hinweis. Laufende Zeitraeume sind unvollstaendig.

Es wird bewusst noch kein Verbrauch pro Vorladung, COP oder Euro-Ersparnis
berechnet: Elektrische Aufnahme umfasst auch Heizung; 1-kWh-Aufloesung ist
fuer einzelne kurze Ladungen grob. Tages-/Monats-/Jahreswechsel, Resets und
Ueberlaeufe verhindern einfache Differenzbildung. Tagesstaende duerfen nicht
rueckwirkend einer einzelnen Vorladung zugeschrieben werden.
Die Temperatur-/Haltezeitauswertung und die Steuerungslogik bleiben unveraendert.

- Laut Rechnung: Weishaupt 200-Liter-Speicher (vermutlich Heizungspuffer; Hydraulik
  nicht bestaetigt) und WES 910 Aqua/Cas fuer Warmwasser mit Trinkwasser-Waermetauscher.
- Hersteller-Gesamtinhalt WES 910: 903 Liter. Der tatsaechlich geladene Bereich und
  die Fuehlerposition sind unbekannt. Daraus wird keine nutzbare Energie abgeleitet.
- Kein separater Stromzaehler. Es gibt keine gemessene Euro-Ersparnis, keinen
  aus Hausverbrauch abgeleiteten Waermepumpenverbrauch und keinen angenommenen COP.
- Jede Anhebung speichert Anlass, Preisvergleich, geplantes Fenster, Bedarfszeit,
  Ausgangssollwert, erhoehten Sollwert und Schreibbestaetigung dauerhaft.
- Der FC04-Verlauf zeigt Temperaturanstieg, Zielerreichung, Ladebetrieb (Status 20),
  naechste erkannte Warmwasserladung und Temperatur zur geplanten Bedarfszeit.
  Ein Ruecksetzen des Sollwerts ist kein aktives Abkuehlen des Speichers.
- Eine erfolgreiche Ueberbrueckung bedeutet: erhoehter Istwert wurde erreicht,
  lueckenloser Verlauf bis zur Bedarfszeit, dort mindestens Ausgangssollwert,
  keine erkannte erneute Warmwasserladung. Das ist ein beobachteter Verlauf,
  kein Nachweis kausal vermiedenen Verbrauchs. Zapfungen bleiben unbekannt.
- Messluecken ueber 15 Minuten, fehlende Zielerreichung, neue Anhebung oder
  manuelle Eingriffe machen die entsprechende Auswertung unvollstaendig.
- Anfangs gelten 6 Stunden Ueberbrueckung und 60 Minuten Ladedauer als explizite
  Annahmen. Die Ueberbrueckungszeit beginnt nach dem Laden; die Ladedauer kommt
  bei der Suche nach dem fruehesten Fenster hinzu. Ab drei brauchbaren Verlaeufen kann die beobachtete Zeit bis zum
  Unterschreiten des Ausgangssollwerts/erneuten Laden die Ueberbrueckungszeit
  konservativ verkuerzen (unteres Quartil, 1 bis 12 Stunden).
  Die Ladedauer verwendet ab drei Zielerreichungen den Median plus 15 Minuten
  Reserve (15 bis 180 Minuten). Speicherverluste und Entnahme werden nicht getrennt.
- Verlauf und Auswertungen erscheinen auf der Heizungsseite und unter
  `GET /api/weishaupt/hot-water`. Maximal 60 Anhebungen im Steuerzustand,
  Detailauswertung fuer die letzten 14 Tage innerhalb der Telemetrieaufbewahrung.
- Quellen: https://www.weishaupt.de/uploads/tx_weishaupt_documents/documents/83149101.pdf
  und https://www.weishaupt.de/uploads/tx_weishaupt_documents/documents/83280801.pdf.

## Stündlicher Stromverbrauch und Zeitraumvergleich

Die Heizungsseite zeigt den Stromverbrauch der gesamten Wärmepumpe für 24 Stunden,
7 oder 30 Tage neben dem unmittelbar vorherigen gleich langen Zeitraum. Über das
Zeitraumende können auch historische Zeiträume vor und nach einer Änderung geprüft
werden. Außentemperatur und Datenabdeckung werden zum Vergleich mit angezeigt;
eine Verbrauchsänderung ist kein kausaler Nachweis einer Einsparung.

Die vorhandene SQLite-Telemetrie enthält die Stromzählerstände bei jedem erfolgreichen
Modbus-Abruf (Standard 300 Sekunden). Das wirksame Intervall ist auf 60–3600 Sekunden
begrenzt; neue Einstellungen außerhalb dieser Grenzen werden abgewiesen. Die
Erfassung muss aktiviert und die Wärmepumpe erreichbar sein. Die Aufbewahrung umfasst
auch die Stromzähler (Standard 400 Tage); nach Ablauf gelöschte Daten werden nicht
rekonstruiert. Es wird keine zusätzliche Datenbank oder zweite Messschleife benötigt.

`GET /api/weishaupt/energy-history?days=7&endsAtUtc=2026-10-22T12:00:00Z`
unterstützt `days=1|7|30` (Standard 7) und ein optionales historisches Ende innerhalb
des letzten Jahres. Das Ende wird auf eine abgeschlossene UTC-Stunde abgerundet.
Jede Stunde enthält die Differenz der ersten Messungen ihrer beiden Stundengrenzen
sowie die tatsächlichen Messzeitpunkte; das Messintervall muss 30–90 Minuten lang sein.
Fehlende Stunden oder Zähler, Rücksetzungen und größere Lücken ergeben `null`, niemals
künstliche Nullen. Die aktuelle unvollständige Stunde bleibt ausgeschlossen.
Der Jahreszähler überbrückt Tages- und Monatswechsel; ersatzweise werden Tageszähler
verwendet, bei Mitternacht einschließlich des abgeschlossenen Vortageszählers.
Zählerwechsel richten sich nach Europe/Berlin, Diagrammzeiten nach der Ortszeit des
Browsers. UTC-Stunden bleiben beim Sommerzeitwechsel eindeutig und gleich lang.

Die internen Stromzähler haben nur 1-kWh-Auflösung. Stündliche Differenzen sind daher
grob und zeitlich vom Abfrageintervall abhängig. Summen beziehen sich ausschließlich
auf verfügbare Intervalle. Prozentvergleiche erscheinen nur bei vollständigen Daten
beider Zeiträume und einem positiven Vorverbrauch. Die Messung unterscheidet nicht
zwischen Strom für Heizung und Warmwasser.
