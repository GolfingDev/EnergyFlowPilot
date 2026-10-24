# Anmeldung und Zugriff von außen

Stand 24.10.2026. Ziel: Zugriff ohne Tailscale-App und ohne DynDNS (beim Internetanbieter nicht möglich), z. B. für Familienmitglieder, mit Anmeldung über Microsoft Entra ID und zwei Rollen.

## Überblick

- **Anmeldung:** Microsoft Entra ID (OpenID Connect, Autorisierungscode mit PKCE) im Backend. Die Sitzung liegt in einem HttpOnly-, Secure- und SameSite=Lax-Cookie (`efp.session`, 12 Stunden gleitend). Der Browser hält keine Tokens.
- **Rollen** (App-Rollen in Entra, Wert exakt so):
  - `Admin`: alles sehen und ändern (Einstellungen, manuelle Ladung, gemeinsame Steuerung, Rücknahme bestätigen).
  - `Viewer` (Anzeige „Betrachter“): alles sehen, nichts ändern.
- **Durchsetzung im Backend:** Jeder Endpunkt und der SignalR-Kanal verlangen mindestens `Viewer`. Jede ändernde HTTP-Methode (POST, PUT, PATCH, DELETE) verlangt zentral `Admin` (`AdminWriteGuardMiddleware`). Neue Endpunkte sind damit automatisch geschützt. Ausnahmen: `/health` (anonym, für den Update-Check), `/api/auth/*` und die SignalR-Verbindungsaushandlung.
- **Frontend:** Ohne Sitzung leitet es zur Anmeldung weiter. Betrachter sehen keine Einstellungen, und Steuer-Knöpfe sind ausgeblendet oder melden „Nur mit der Rolle Admin möglich“.
- **Erreichbarkeit:** Tailscale Funnel veröffentlicht den Pi unter `https://<pi-name>.<tailnet>.ts.net`. Dafür sind keine Portfreigabe, kein DynDNS und keine öffentliche IPv4 nötig; das Zertifikat kommt automatisch. Besucher brauchen kein Tailscale.
- **Immer anmelden:** Auch im Heimnetz gibt es nur die Anmeldung über die öffentliche Adresse. Wer `http://<pi-ip>` öffnet, wird zur Anmeldung dorthin umgeleitet. Notzugang bei Internet- oder Entra-Ausfall ist SSH auf den Pi; die Steuerung läuft davon unabhängig weiter.

**Reihenfolge:** Erst die Anmeldung aktivieren und prüfen, dann Funnel einschalten. Ohne Anmeldung (`Authentication:Mode` nicht `Entra`) ist das System offen wie bisher und protokolliert beim Start eine Warnung. So darf es nicht öffentlich erreichbar sein.

## 1. Tailscale-Adresse ermitteln

Auf dem Pi:

```bash
tailscale status --json | jq -r '.Self.DNSName'   # z. B. elektrosklave.tail1234.ts.net.
```

Die öffentliche Basisadresse ist dann `https://elektrosklave.tail1234.ts.net` (ohne Punkt am Ende, ohne Pfad).

## 2. Entra-App registrieren (Azure-Portal, eigener Tenant)

1. **Entra ID → App-Registrierungen → Neue Registrierung**
   - Name: `EnergyFlowPilot`
   - Unterstützte Kontotypen: *Nur Konten in diesem Organisationsverzeichnis*
   - Umleitungs-URI: Plattform **Web**, `https://<pi-name>.<tailnet>.ts.net/api/auth/signin-oidc`
2. **Authentifizierung**
   - URL für Front-Channel-Abmeldung: `https://<pi-name>.<tailnet>.ts.net/api/auth/signout-callback-oidc`
   - Unter *Umleitungs-URIs* zusätzlich dieselbe Abmelde-URL eintragen.
   - Implizite Gewährung: nichts aktivieren.
3. **Zertifikate & Geheimnisse → Neuer geheimer Clientschlüssel**: Den *Wert* sofort kopieren. Ablaufdatum notieren und vor Ablauf erneuern.
4. **App-Rollen → App-Rolle erstellen** (zweimal), zulässige Mitgliedstypen *Benutzer/Gruppen*:
   - Anzeigename `Admin`, Wert `Admin`, Beschreibung „Darf alles sehen und ändern“.
   - Anzeigename `Betrachter`, Wert `Viewer`, Beschreibung „Darf nur ansehen“.
5. **Unternehmensanwendungen → EnergyFlowPilot → Eigenschaften**: *Zuweisung erforderlich* = **Ja**. Nur zugewiesene Personen erhalten überhaupt eine Anmeldung.
6. **Unternehmensanwendungen → EnergyFlowPilot → Benutzer und Gruppen → Benutzer/Gruppe hinzufügen**:
   - Dein Konto mit der Rolle `Admin`.
   - Weitere Personen mit `Betrachter`.
7. Von der Übersichtsseite der App-Registrierung **Verzeichnis-ID (Mandant)** und **Anwendungs-ID (Client)** notieren.

### Externe Person (z. B. Vater) einladen

**Entra ID → Benutzer → Neuer Benutzer → Externen Benutzer einladen** mit seiner E-Mail-Adresse. Er bestätigt die Einladung per Mail und kann sich mit seinem bestehenden Microsoft-Konto (oder Einmalcode per Mail) anmelden. Danach wie in Schritt 6 die Rolle `Betrachter` zuweisen.

## 3. Pi konfigurieren

In `/etc/energyflowpilot/api.env` (gehört root, nur für die Dienstgruppe lesbar, nicht im Repository):

```bash
Authentication__Mode=Entra
Authentication__TenantId=<Verzeichnis-ID>
Authentication__ClientId=<Anwendungs-ID>
Authentication__ClientSecret=<Geheimer Clientschlüssel>
Authentication__PublicBaseUrl=https://<pi-name>.<tailnet>.ts.net
```

```bash
sudo systemctl restart energyflowpilot-api
journalctl -u energyflowpilot-api -n 30
```

Bei unvollständigen Werten startet der Dienst bewusst nicht und nennt den fehlenden Teil im Log. Das Client-Geheimnis gehört absichtlich nicht in die Einstellungsdatenbank: Es wird vor jeder Anmeldung gebraucht und darf über die Weboberfläche weder lesbar noch änderbar sein.

Die Sitzungs-Cookies werden mit dem Data-Protection-Schlüsselbund neben der Datenbank (`data-protection-keys`) verschlüsselt. Nach einem Neustart bleiben Anmeldungen gültig, solange der Schlüsselbund erhalten bleibt.

## 4. Tailscale Funnel einschalten

Einmalig in der Tailscale-Admin-Konsole:

- **DNS**: MagicDNS und **HTTPS-Zertifikate** aktivieren.
- **Access controls**: Funnel für den Pi erlauben, z. B.

  ```json
  "nodeAttrs": [{ "target": ["<pi-name oder tag>"], "attr": ["funnel"] }]
  ```

Auf dem Pi (dauerhaft, übersteht Neustarts):

```bash
sudo tailscale funnel --bg 80
tailscale funnel status
```

Funnel nimmt HTTPS auf Port 443 an und leitet unverschlüsselt an nginx auf Port 80 des Pi weiter. Die App erkennt Anfragen für den öffentlichen Namen, auch über `X-Forwarded-Host`, und behandelt sie als HTTPS. Abschalten: `sudo tailscale funnel --https=443 off`.

## 5. Prüfen

1. `https://<pi-name>.<tailnet>.ts.net` im Browser öffnen: Die Microsoft-Anmeldung erscheint, danach das Dashboard. Oben rechts stehen Name und Rolle.
2. Mit einem `Betrachter`-Konto: Einstellungen fehlen, Steuer-Knöpfe sind ausgeblendet. Ein direkter Änderungsversuch an der API liefert 403.
3. Ohne Anmeldung, z. B. im privaten Fenster: `https://…/api/settings` liefert 401.
4. Ein Konto ohne Rollenzuweisung bekommt die Seite „Kein Zugriff“.

## Grenzen

- Die Adresse ist öffentlich bekannt und im Internet erreichbar. Der Schutz hängt vollständig an der Entra-Anmeldung; Mehrfaktor-Anmeldung im Tenant wird dringend empfohlen.
- Die bisherigen Dashboard-Varianten zeigen die Knöpfe der Victron-Einzelladung auch Betrachtern. Ein Klick wird abgewiesen und erklärt; ausgeblendet sind sie nur auf der Karte der gemeinsamen Steuerung und in der Navigation.
- Ändert sich der Tailscale-Name, müssen Umleitungs-URIs in Entra und `Authentication__PublicBaseUrl` angepasst werden.
