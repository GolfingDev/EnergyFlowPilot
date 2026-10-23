# Lokalen E3/DC-Zugriff prüfen

Die bestehende Hager-Cloud-Anbindung liest Telemetrie. Ein OAuth-Token für die Cloud bestätigt keinen lokalen RSCP-Zugang und keine Schreibrechte.

Die Open-Source-Projekte [python-e3dc](https://github.com/fsantini/python-e3dc) und [E3DC-Control](https://github.com/Eba-M/E3DC-Control) implementieren lokale RSCP-Steuerbefehle, unter anderem Leistungsgrenzen und Betriebsmodi. Das ist ein Beleg für Protokollfunktionen, keine Kompatibilitätszusage für das installierte Gerät oder dessen Firmware.

## Voraussetzungen vor Ort

- E3/DC-Modell und Firmware ermitteln.
- Lokale Geräteadresse und aktivierten RSCP-Zugang prüfen; typischer TCP-Port ist 5033.
- RSCP-Passwort bzw. Verschlüsselungsschlüssel und Benutzerzugang lokal bereitstellen. Keine Zugangsdaten in Chat, Diagnoseausgabe oder Git ablegen.

Auf dem Raspberry Pi im selben Netz lässt sich zunächst ausschließlich die TCP-Erreichbarkeit prüfen:

```bash
python3 scripts/diagnostics/check-e3dc-rscp.py <lokale-e3dc-adresse>
```

Die Prüfung öffnet und schließt nur eine TCP-Verbindung, ohne Anwendungsdaten zu senden. Ein erfolgreicher TCP-Verbindungsaufbau bestätigt weder RSCP-Anmeldung noch Schreibfähigkeit. Danach sind zunächst authentifizierte Leseabfragen von Gerätedaten, Betriebsmodus und Leistungsgrenzen erforderlich. Das Skript führt diese nicht aus.

## Noch ausstehende Schreibprüfung

Am 23.10.2026 wurde ein authentifizierter lokaler RSCP-Lesezugriff auf die physische Anlage erfolgreich durchgeführt. Status, Firmware, Systemeigenschaften, Leistungsgrenzen und Notstromreserve konnten gelesen werden. Zugangsdaten werden hier nicht dokumentiert. Die ersten ausdrücklich beauftragten Ladetests wurden wegen ausbleibender E3/DC-Ladung abgebrochen. Ein weiterer Test bestätigte anschließend gleichzeitige Ladung beider Speicher, endete jedoch vorzeitig wegen eines Kommunikationsfehlers (siehe unten). Die spaeter implementierte gemeinsame Steuerung ist unten beschrieben und noch nicht auf der Anlage in Betrieb genommen.

Ausgelesener Stand (Momentaufnahme, vor Steuerung erneut prüfen):

- Firmware: `S10_2026_027`.
- Installierte Batteriekapazität laut Systemeigenschaften: 16.632 Wh. Die zunächst genannten 12 kWh wurden anschließend durch eine detaillierte Leseabfrage ergänzt (siehe unten); installierte Kapazität und nutzbare Energie sind zu unterscheiden.
- Gemeldete maximale Lade- und Entladeleistung: jeweils 3.000 W.
- Gespeicherte Lade- und Entladegrenzen: jeweils 1.500 W; `EMS_POWER_LIMITS_USED` ist jedoch `false`. Die gespeicherten Werte sind deshalb keine bestätigten aktiven Grenzen.
- Wetterbasierte Ladung und Stromsparfunktion sind aktiviert.
- Notstromreserve: 1.513 Wh; der vom Gerät gemeldete Prozentwert liegt bei etwa 10,8 %. Dessen Bezugsgröße ist gerätespezifisch und darf nicht ungeprüft auf die 12 kWh Planungskapazität übertragen werden.

### Detaillierte Kapazitätsabfrage am 23.10.2026, 19:01 Uhr (Europe/Berlin)

- Batterieindex 0: `BJ-DCB06B-AA`, drei DCB-Module.
- `BAT_DESIGN_CAPACITY`: 330 Ah; `BAT_DESIGN_VOLTAGE`: 50,4 V. Das ergibt die gemeldeten 16.632 Wh installierte Nennenergie.
- `BAT_USABLE_CAPACITY`: 278,0607 Ah. Mit der Nennspannung ergibt sich rechnerisch eine nutzbare Energie von **14.014,26 Wh**; dieser Wert stimmt mit `SE_PARAM_EP_RESERVE_MAX_W` überein.
- Abzüglich der unverändert eingestellten Notstromreserve von 1.513 Wh verbleiben rechnerisch **12.501,26 Wh** für den regulären Betrieb zwischen Reserve und Vollladung. Das ist ein aus Gerätemeldungen berechneter Energieumfang, keine gemessene AC-Entladeenergie; Umwandlungsverluste sind zusätzlich zu berücksichtigen.
- `BAT_RSOC`: 10,8864 %, EMS-Anzeige: 10 %. `BAT_USABLE_REMAINING_CAPACITY`: 30,2707 Ah. Der Speicher liegt damit ungefähr an der Notstromreserve; die 12,50 kWh sind nicht die momentan verfügbare Restenergie.
- Batteriemodul-Spezifikation: 4.500 W Lade-/Entladeleistung; EMS-Systemgrenzen bestätigen jedoch jeweils **3.000 W**. Für Steuerbefehle gilt die kleinere Systemgrenze, nicht die höhere Modulspezifikation.

Für die gemeinsame Planung sollen Kapazität, Reserve und gültige Leistungsgrenzen aus aktuellen Gerätewerten übernommen werden. Entweder werden 14,014 kWh mit separater Reserve von 1,513 kWh modelliert oder rund 12,501 kWh oberhalb der Reserve; die Reserve darf nicht doppelt abgezogen werden. Die bisherige pauschale Angabe von 12 kWh ist kein fest hinterlegter Geräteparameter.

### Abgebrochener Ladetest am 23.10.2026, 19:08 Uhr (Europe/Berlin)

Beauftragt waren fünf Minuten Netzladung mit jeweils 500 W für beide Akkus. Vorher waren beide Batterieleistungen 0 W und keine manuelle Victron-Ladung aktiv. Der laufende Controller wurde über seine vorhandene HTTP-API erreicht; der Victron-MQTT-Zugang lieferte nach einer Keepalive-Abfrage frische Messwerte.

Zuerst wurde ausschließlich E3/DC über `EMS_REQ_SET_POWER` mit temporärem Modus 4 (`grid_charge`) und 500 W angesprochen. Datentypen: Modus `UChar8`, Leistung `Int32`. Der Befehl wurde während einer Anlaufbeobachtung etwa alle zwei Sekunden erneuert. Die Antwort war `EMS_SET_POWER`, `Uint32`, Wert 97; die gemessene Batterieleistung blieb über etwa 18 Sekunden bei 0 W. Diese Antwort bestätigt keine tatsächlich umgesetzte 500-W-Ladung.

Deshalb wurde vor dem Start der manuellen Victron-Ladung abgebrochen. Beide Akkus wurden nicht fünf Minuten gemeinsam geladen. Anschließend bestätigte E3/DC den temporären Automatikbefehl (Modus 0, Leistung 0) mit Antwortwert 0. Rücklesung bestätigte unveränderte Leistungs-/Wetterregelungseinstellungen und Notstromreserve. Beide Batterieleistungen lagen danach bei 0 W; keine manuelle Victron-Ladung war aktiv.

Die anschließende reine Leseprüfung meldete `EMS_USED_CHARGE_LIMIT = 97 W`, Batterie- und DC/DC-Ladegrenzen jeweils 3.000 W, gespeicherte Benutzerladegrenze 1.500 W und deaktivierte Sperrzeiten. Die Ursache der effektiven 97-W-Grenze und des ausbleibenden Ladestarts ist noch nicht geklärt. Wetterregelung, dauerhafte Leistungsgrenzen und Reserven wurden für den Test nicht geändert. Vor weiterer Inbetriebnahme sind die effektive Begrenzung und die für diese Firmware passende Netzladefunktion zu klären.

Nach der vom Betreiber gemeldeten AI360-Abschaltung zeigte die Leseprüfung weiterhin 97 W effektive Ladegrenze und aktivierte wetterbasierte Ladung. Auf ausdrücklichen Wunsch wurde der Test am 23.10.2026 um 19:11 Uhr wiederholt. Der gleiche temporäre 500-W-Netzladebefehl ergab erneut Antwortwert 97; nach rund zwölf Sekunden wurde wegen gemessener Batterieleistung von -69 W abgebrochen. Die Victron-Testladung wurde nicht gestartet. Rückkehr auf E3/DC-Automatik wurde mit Antwortwert 0 bestätigt; Einstellungen und Reserve blieben laut Rücklesung unverändert. Um 19:11:28 Uhr lagen beide Batterieleistungen bei 0 W und keine manuelle Victron-Ladung war aktiv. Eine Ursache durch AI360 ist damit weder belegt noch ausgeschlossen.

Die nachträgliche Prüfung des Testskripts gegen [hacs-e3dc](https://github.com/torbennehmer/hacs-e3dc/blob/main/custom_components/e3dc_rscp/e3dc_proxy.py) und die [RSCP-Tagdefinitionen von ioBroker](https://github.com/git-kick/ioBroker.e3dc-rscp/blob/master/lib/RscpTags.json) bestätigt die verwendeten Felder und Datentypen: Netzlademodus 4 (`UChar8`) und positiver Leistungswert 500 (`Int32`). Negative Werte von `EMS_POWER_BAT` bedeuten Entladung. Ein vertauschtes Leistungsvorzeichen oder expliziter Entlademodus ist im Testskript nicht erkennbar. Allerdings wurde `EMS_REQ_MODE` während des Testbefehls nicht zurückgelesen. Der Antwortwert 97 ist keine Bestätigung des gewünschten 500-W-Zustands. Vor einem weiteren Test müssen Antwort, tatsächlich aktiver Betriebsmodus, effektive Grenzen und gemessene Leistung gemeinsam protokolliert werden; die kleine Entladung allein belegt weder eine umgekehrte Befehlswirkung noch deren Ursache.

### Gleichzeitige Ladung nachgewiesen, Kommunikationsfehler kurz vor Testende

Auf ausdrückliche Freigabe wurde am 23.10.2026 ab 19:15 Uhr erneut getestet, diesmal mit bis zu 90 Sekunden Anlaufbeobachtung und zusätzlicher zyklischer Rücklesung von `EMS_MODE` und `EMS_USED_CHARGE_LIMIT`. Die flüchtigen E3/DC-Befehlsfelder blieben identisch. Bereits vor dem ersten Schreibbefehl meldete die Anlage jetzt 3.000 W effektive Ladegrenze. Die Antwort auf 500 W lautete diesmal 500, der zurückgelesene Betriebsmodus wechselte von 1 auf 2 und die Batterieleistung wurde nach etwa zwei Sekunden positiv (366 W). Nach drei passenden Messungen wurde die Victron-Ladung über die bestehende manuelle API für fünf Minuten mit 0,5 kW angefordert. Der lange Anlaufzeitraum wurde nicht benötigt. Die zeitliche Ursache der geänderten Begrenzung ist damit weiterhin nicht belegt.

Gemeinsame Testphase ab 19:15:39,99 Uhr (Europe/Berlin):

- 139 Messpunkte über 282,5 Sekunden, bis 19:20:22,44 Uhr.
- E3/DC nach den ersten 20 Sekunden: 495 bis 498 W, Mittelwert 495,9 W.
- Victron nach den ersten 20 Sekunden: 338 bis 472 W, Mittelwert 448,4 W bei 500 W Sollwert. Die Messwerte belegen keine exakt eingehaltene 500-W-DC-Ladeleistung des Victron.
- Aus den erfassten Leistungswerten zeitlich integriert: etwa 38,9 Wh E3/DC und 33,1 Wh Victron. Die Werte sind Näherungen aus Telemetrie, keine geeichten Energiemessungen.
- Beide EMS-Ladestandsanzeigen stiegen von 10 auf 11 %. Diese gerundeten Anzeigen ersetzen keine Energiemessung.

Um 19:20:24,50 Uhr, rund 15 Sekunden vor dem geplanten Ende, meldete der RSCP-Client einen `CommunicationError`. Das Skript brach ab, entfernte um 19:20:25,04 Uhr die manuelle Victron-Ladung und stellte nach Wiederverbindung um 19:20:25,12 Uhr E3/DC auf Automatik (Antwortwert 0). Rücklesung bestätigte unveränderte E3/DC-Einstellungen und Notstromreserve sowie inaktive manuelle Victron-Ladung. Abschließende Messwerte um 19:20:33 Uhr: E3/DC 0 W, Victron -319 W nach Rückgabe an die vorherige automatische Regelung.

Damit sind die kurzfristige RSCP-Netzladung und gleichzeitige Ladung beider Akkus nachgewiesen. Ein unterbrechungsfreier Fünf-Minuten-Test und robuste Behandlung von Kommunikationsfehlern sind weiterhin offen. Der Test ist keine Freigabe für unbeaufsichtigte gemeinsame Steuerung.

Vor einem späteren kontrollierten Schreibtest müssen die ursprünglichen Einstellungen gelesen und für die Wiederherstellung gesichert werden. Zu prüfen sind insbesondere, ob die Firmware eine Entladesperre unterstützt, ob sie diese per Rücklesung bestätigt und wie sie sich bei Verbindungsabbruch oder Prozessende verhält. Eine bestätigte Begrenzung und deren Wiederherstellung gehören zur Steuerung; ein offener TCP-Port reicht dafür nicht.

Für koordinierte Netzladung wäre die Reihenfolge: E3/DC-Entladung sperren, Bestätigung abwarten, Victron-Netzladung freigeben, Victron-Netzladung beenden und ursprüngliche E3/DC-Einstellungen wiederherstellen. Bei fehlender Bestätigung darf die Netzladung nicht starten. Kein zweiter unabhängiger Optimierer darf gleichzeitig dieselben Einstellungen verändern. Der bestehende Konfliktschutz bleibt bis zur nachgewiesenen Koordination aktiv.

## Implementierte gemeinsame Steuerung (standardmaessig deaktiviert)

Der native .NET-Adapter verwendet RSCP mit Rijndael-CBC (256-Bit-Blockgroesse) ueber `BouncyCastle.Cryptography`. Lesen und Schreiben verwenden getrennte CBC-Zustaende, vollstaendige Rahmen, CRC-Pruefung, erwartete Tags und gepruefte Anmeldung. Fragmentierte und zusammen ankommende TCP-Rahmen sind durch unabhaengig erzeugte Testdaten abgedeckt. Das beweist noch keine Behebung jeder Ursache des frueheren Kommunikationsfehlers. Lesezugriffe haben zehn Sekunden Frist, temporaere Schreibbefehle zwei Sekunden, jeweils zusaetzlich durch den Aufrufer begrenzt.

Lesen und Schreiben teilen sich eine angemeldete RSCP-Verbindung (`E3dcRscpSession`), statt sich pro Befehl neu anzumelden; bei gemeinsamer Steuerung waeren das sonst rund zwei Anmeldungen pro Sekunde. Anfragen werden strikt nacheinander ausgefuehrt. Nach jedem Fehler oder Abbruch wird die Verbindung verworfen, weil die CBC-Kette dann undefiniert ist. Schliesst die Anlage eine wiederverwendete Verbindung, folgt genau eine neue Anmeldung mit derselben (idempotenten) Anfrage. Aendern sich Adresse oder Zugangsdaten, wird neu verbunden.

### Konfiguration und Freigabe

1. Unter lokalem E3/DC-Zugang Adresse, Port 5033, Benutzer, Passwort und RSCP-Schluessel eintragen. Zugangsdaten werden nicht vorbelegt. `e3dc.rscp.enabled=true` aktiviert lokale Messwerte.
2. `jointBattery.maximumGridImportWatts=0` bedeutet gemaess Betreiberwunsch **keine zusaetzliche Gesamtleistungsgrenze**. Positive Werte begrenzen Haus und beide Ladevorgaenge gemeinsam. Geraetegrenzen und elektrische Anschlussgrenzen gelten weiterhin.
3. `jointBattery.shadowEnabled=true` aktiviert die Vorschau ohne Hardwarebefehle. Zwei getrennte Energiezustaende werden mit dynamischer Programmierung und interpolierten Folgekosten geplant; das ist eine numerische Naeherung. Reserve, Kapazitaet und wirksame E3/DC-Leistungsgrenzen stammen aus lokalen Messwerten. Wirkungsgrad bleibt eine sichtbare, konfigurierbare Annahme. Geschaetzte Preise erlauben keine Netzladung.
4. Vor realer Ausfuehrung sind `jointBattery.phase1MaximumImportWatts`, `phase2MaximumImportWatts`, `phase3MaximumImportWatts` sowie `jointBattery.victronMaximumAcInputWatts` (je gesteuerter Phase) mit bestaetigten Anschlussgrenzen zu belegen. Default 0 blockiert reale Aktionen. Eine gemeldete Victron-Stromgrenze ist kein Nachweis der Leitungsabsicherung. Frische Netzleistungen aller drei Phasen sowie AC-in/AC-out werden benoetigt. Die E3/DC-AC-Leistung wird fuer diese dreiphasige S10-Anlage symmetrisch angesetzt; die laufenden Phasenmessungen und Sicherheitsabstaende begrenzen die Ladung.
5. Die aktualisierten Linux-Deploymentskripte installieren systemd `Type=notify`, `WatchdogSec=15`, `WatchdogSignal=SIGKILL` und separate `--joint-stop`-Helfer vor Start und nach Stop. Der Ausfuehrungsworker selbst sendet den Watchdog-Impuls. Jeder Zyklus hat ein festes Zeitbudget (4 s Zyklus, 6 s Fehlerbehandlung); das Warten auf die mit der Einzelakku-Steuerung geteilte Schreibsperre ist darin enthalten und laesst bei Zeitueberschreitung nur einen Takt aus. Impulse liegen damit hoechstens etwa 7 s auseinander, sodass langsame Geraete oder Netzwerke den Dienst nicht beenden. Ein echter Prozessstillstand wird ausserhalb des Prozesses erkannt; die Ruecknahme versucht ein separater Prozess. Ein fehlgeschlagener Helfer vor dem Start (`ExecStartPre=-`) blockiert den Dienst nicht mehr: Der gestartete Prozess haelt die Einzelakku-Steuerung gesperrt und setzt die Ruecknahme fort. Unter Windows oder ohne Watchdog von hoechstens 15 s bleibt reale Ausfuehrung gesperrt.
6. Erst nach der kontrollierten Hardwareabnahme `jointBattery.executionEnabled=true` und `victron.dryRun=false` setzen. Die gemeinsame Planung laeuft dann auch ohne separates Shadow-Flag. Globaler Dry-run und deaktivierte Ausfuehrung senden keine neuen Lade-/Entladebefehle; bereits bestehende Wiederherstellungspflichten werden abgearbeitet.

### Ausfuehrung und Wiederherstellung

Ein gemeinsamer Besitzer sperrt Einzelakku-Publisher, deren Refresh und manuelle Ladestarts. Der Sperrstatus ueberlebt Neustarts als internes `jointBattery.recoveryPending`; die Einstellungs-API darf ihn nicht loeschen. Geraeteadressen und Zugangseinstellungen sind bei aktiver Steuerung oder ausstehender Ruecknahme gegen Aenderungen gesperrt.

Die Steuerung wird erst uebernommen, wenn alle Startbedingungen ohne Hardwarezugriff erfuellt sind: Watchdog, aktivierter und vollstaendig konfigurierter RSCP-Zugang, Victron im External-ESS-Modus, alle Anschlussgrenzen groesser 0 und ein aktueller Plan mit gueltigem Zeitabschnitt. Fehlt eine Bedingung, zeigt das Dashboard den Status `Blocked` mit Grund. Es entsteht keine Wiederherstellungspflicht, und es wird nichts geschrieben. So kann eine unerfuellbare Ruecknahme nicht die Einstellungen sperren, die zur Behebung noetig waeren.

Bleibt eine Ruecknahme dauerhaft unbestaetigt (z. B. E3/DC nicht erreichbar), gibt es einen Notausgang: gemeinsame Steuerung deaktivieren, beide Speicher vor Ort pruefen und im Dashboard „Ruecknahme manuell bestaetigen“ waehlen (`POST /api/joint-battery/recovery/confirm`). Das loescht die Wiederherstellungspflicht unter derselben Sperre wie die Ausfuehrung und gibt die Einzelakku-Steuerung frei. Der Vorgang wird als Betriebsereignis `JointBatteryRecoveryOverride` protokolliert. Ein nicht erneuerter E3/DC-Leistungsbefehl faellt laut RSCP-Spezifikation nach spaetestens 30 Sekunden selbst in den Normalbetrieb zurueck.

Die Ausfuehrung laeuft im Sekundentakt mit begrenztem Zyklusbudget. Vor einem Richtungswechsel werden beide Speicher neutral gestellt; zwei neutrale Rueckmeldungen sind Voraussetzung fuer die naechste Aktion. E3/DC wird zuerst angesprochen, Leistungs-Echo und Betriebsmodus werden geprueft, erst dann darf Victron folgen. Modusverzoegerungen haben eine eigene Anlauffrist; Victron bleibt bis zur Bestaetigung neutral. Leistungsanstiege erfolgen mit 250 W je Zyklus, Reduktionen sofort. Messwerte, Reserve, aktuelle Geraetegrenzen, Hausbedarf, AC-out-Last und Phasengrenzen begrenzen jedes Ziel erneut. Netzeinspeisung bei Akkuentladung fuehrt zum Stopp.

Geschrieben wird ausschliesslich der fluechtige RSCP-Leistungsbefehl: Modus 1 neutral, 4 Netzladung, 3 PV-Ladung, 2 Entladung, 0 Automatik. Keine dauerhafte Wetterregelung, Reserve oder Leistungseinstellung wird veraendert. Die Rueckleseskala `EMS_MODE` ist eine andere als die Befehlsskala: 0 IDLE, 1 DISCHARGE, 2 CHARGE (Quelle: ioBroker.e3dc-rscp, `rscpEmsMode`). Erwartet werden daher 0 neutral, 1 Entladung und 2 fuer Netz- **und** PV-Ladung. Die fruehere Annahme „1 bei PV-Ladung“ war falsch und haette jede PV-Ladung nach 90 Sekunden abgebrochen. Netzladung mit Rueckmeldung 2 ist am 23.10.2026 beobachtet worden; PV-/Entlademodi und ihre Rueckmeldung muessen auf dieser Firmware noch physisch abgenommen werden.

Bei Fehlern, abgelaufenen Plaenen oder Abschaltung stoppt zuerst Victron, danach E3/DC. Erst neutrale Messwerte beider Speicher erlauben die Rueckgabe von E3/DC an Automatik und das Loeschen der Wiederherstellungspflicht. Scheitert dies, bleiben Einzelakku-Schreiber gesperrt. Nach Fehlern gilt eine Wiederanlaufsperre von 30 Sekunden. Beim Neustart erfolgt zuerst die ausstehende Ruecknahme; scheitert sie, startet der Dienst trotzdem mit gesperrter Einzelakku-Steuerung. Ist die Hardware nicht erreichbar, kann auch der separate Helfer keinen sicheren physikalischen Zustand garantieren; ein vollstaendiger Netzwerkausfall ist damit nicht als beherrscht nachgewiesen.

### Gemeldete E3/DC-Leistungsgrenze (97 W)

An der Notstromreserve meldet der E3/DC als wirksame Lade- und Entladegrenze (`EMS_USED_CHARGE_LIMIT`/`EMS_USED_DISCHARGE_LIMIT`) zeitweise nur ±97 W, obwohl seine Systemgrenze 3 kW betraegt. Die Ursache ist nicht geklaert; vermutet wird die aktive Stromsparfunktion. Betreiberentscheidung vom 23.10.2026: ausprobieren, was das Geraet bei hoeherer Anforderung macht.

- Standard (`jointBattery.e3dc.respectReportedLimits=false`): Planung und Ausfuehrung fordern bis zur Systemgrenze an. Die Anforderung steigt wie ueblich in 250-W-Schritten, jeweils ausgehend von der zuletzt **gewaehrten** Leistung.
- Die Leistungsquittung von `EMS_SET_POWER` gilt als tatsaechlich gewaehrte Leistung. Weniger als angefordert ist kein Fehler: Die Steuerung rechnet mit dem gewaehrten Wert weiter, und die Karte zeigt „E3/DC gibt nur X von Y W frei“. Mehr als angefordert gilt als unplausibel und stoppt die Ausfuehrung. Phasen- und Zuleitungsgrenzen werden immer fuer die volle Anforderung berechnet, daher ist eine kleinere Freigabe unkritisch.
- Gewaehrt der E3/DC 0 W, wird nicht auf seinen Betriebsmodus gewartet; der Victron arbeitet weiter.
- Mit `true` gilt wieder die gemeldete Grenze als Obergrenze. Die gemeldete Grenze steht auf der Dashboard-Karte.

### Manuelle Ladung beider Akkus

Bei eingeschalteter gemeinsamer Steuerung (ohne globalen Shadow-Modus) kann im Dashboard eine Lademenge in kWh vorgegeben werden, optional mit einer Hoechstleistung (`POST`/`DELETE /api/joint-battery/manual-charge`). Betreiberentscheidung vom 23.10.2026:

- Gemessen wird die **im Akku gespeicherte** Energie: Zuwachs von Ladestand x Kapazitaet beider Akkus seit Beginn. Ladeverluste zaehlen nicht mit. Der Ausgangswert wird gespeichert, sodass ein Neustart die Ladung nicht verfaelscht.
- Beide Akkus laden gleichzeitig, aufgeteilt nach freiem Platz und aktuellen Leistungsgrenzen (beim E3/DC die gemeldete wirksame Grenze). Ist einer voll, laedt der andere weiter.
- Ohne Hoechstleistung wird so schnell geladen, wie Geraete-, Phasen- und Victron-Zuleitungsgrenzen erlauben; mit Angabe wird die Summe beider Akkus begrenzt.
- Obergrenze ist das Planungsmaximum je Akku, wie bei geplanter Netzladung.
- Die Ladung ersetzt waehrend ihrer Dauer den wirtschaftlichen Plan und laedt unabhaengig vom Preis. Sie endet bei erreichter Menge, wenn beide Akkus ihr Planungsmaximum erreicht haben, nach einer Sicherheitsfrist von 6 Stunden, per Stopp im Dashboard oder beim Ausschalten der gemeinsamen Steuerung bzw. Aktivieren des Shadow-Modus. Danach uebernimmt im selben Takt wieder der Plan. Der Plan kann die geladene Energie spaeter wirtschaftlich wieder nutzen.
- Der Zustand liegt in den internen Einstellungen `jointBattery.manualCharge.*`, die die Einstellungs-API nicht aendern darf. Ende und Grund werden als Betriebsereignis `JointBatteryManualCharge` protokolliert.

### Anzeige, Verbrauchsmodell und Geheimnisse

Das Dashboard und `GET /api/joint-battery/shadow` zeigen getrennte Messwerte, geplante Ziele, letzte Ausfuehrungsvorgaben und deren Alter. Veraltete Plaene liefern keine gueltigen Ziele. Der bestehende Victron-Forecast und die Live-Entscheidung werden bei gemeinsamer Steuerung aus demselben Plan bzw. Ausfuehrungszustand projiziert; ein zweiter Optimierer steuert dann nicht parallel.

Das gemeinsame Verbrauchsprofil wird aus Rohmessungen als `Netzleistung - Victron-Akkuleistung - E3/DC-Akkuleistung + E3/DC-PV` rekonstruiert. Alte Tagessummen mit unabhaengigem E3/DC-Betrieb werden nicht wiederverwendet. Temperaturkorrektur nutzt geeignete Daten der letzten 14 Tage; bei fehlenden Daten wird das Ersatzprofil sichtbar gekennzeichnet. Langfristige gemeinsame Verbrauchstagessummen sind noch nicht migriert.

Shadow-Vorschlaege und gemeinsame Ausfuehrungszustaende werden als getrennte Betriebsereignisse protokolliert. Die vorhandene Victron-Historie erhaelt die ausgefuehrten gemeinsamen Victron-Ziele. Die bestehende Ersparnisabrechnung bleibt Victron-bezogen und ist keine gemeinsame abrechnungsgenaue Energiebilanz.

Die drei RSCP-Zugangsfelder werden mit ASP.NET Core Data Protection verschluesselt. Der Schluesselbund liegt im Ordner `data-protection-keys` neben der SQLite-Datenbank (auf dem Pi unter `/var/lib/<app>/`) und muss zusammen mit ihr gesichert werden. Ohne passende Schluessel sind die Werte unlesbar. Sie gelten dann als nicht konfiguriert, eine Warnung wird protokolliert, und RSCP bleibt gesperrt, bis die Zugangsdaten neu eingetragen sind. Die uebrigen Einstellungen bleiben lesbar. Vor dieser Aenderung gespeicherte RSCP-Zugangsdaten (Schluessel im Benutzerprofil) muessen einmal neu eingegeben werden. Andere bestehende Zugangsfelder werden nicht migriert; Frontend-Leseantworten enthalten keine geheimen Werte.

### Noch ausstehende Inbetriebnahme

Die Implementierung ist lokal vorbereitet, nicht auf dem Raspberry Pi ausgerollt und nicht dauerhaft aktiviert. Vor Freigabe: Anschlussgrenzen bestaetigen, Watchdog/Helfer auf dem Zielsystem pruefen, begrenzte gemeinsame Lade-/Entladetests inklusive Verzoegerung, Gegenrichtung, Kommunikationsverlust, hartem Prozessabbruch und Neustart ausfuehren. Der fruehere teilweise erfolgreiche Ladetest ersetzt diese Abnahme nicht.
