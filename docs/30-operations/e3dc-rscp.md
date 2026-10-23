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

In der Cloud-Entwicklungsumgebung sind keine lokale Geräteadresse und keine RSCP-Zugangsdaten hinterlegt. Es wurde daher kein Zugriff auf die physische Anlage und kein Schreibtest durchgeführt. Diese Änderung ergänzt keine automatische RSCP-Steuerung.

Vor einem späteren kontrollierten Schreibtest müssen die ursprünglichen Einstellungen gelesen und für die Wiederherstellung gesichert werden. Zu prüfen sind insbesondere, ob die Firmware eine Entladesperre unterstützt, ob sie diese per Rücklesung bestätigt und wie sie sich bei Verbindungsabbruch oder Prozessende verhält. Eine bestätigte Begrenzung und deren Wiederherstellung gehören zur Steuerung; ein offener TCP-Port reicht dafür nicht.

Für koordinierte Netzladung wäre die Reihenfolge: E3/DC-Entladung sperren, Bestätigung abwarten, Victron-Netzladung freigeben, Victron-Netzladung beenden und ursprüngliche E3/DC-Einstellungen wiederherstellen. Bei fehlender Bestätigung darf die Netzladung nicht starten. Kein zweiter unabhängiger Optimierer darf gleichzeitig dieselben Einstellungen verändern. Der bestehende Konfliktschutz bleibt bis zur nachgewiesenen Koordination aktiv.
