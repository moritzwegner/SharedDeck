# Gemeinsam fliegen mit SharedDeck – Anleitung für Host und Co-Pilot

> **Stand: erster Entwurf, noch nie im Simulator getestet.** Gebaut und automatisch getestet sind:
> - App und Installer mit 29 Tests
> - WASM-Modul
> - FBW-Fork mit Typprüfung
>
> Ob alles im Simulator so funktioniert wie geplant, zeigt erst euer erster gemeinsamer Test. Startet deshalb im **Abstimmungsmodus** (siehe Schritt 6).

## 1. Was jeder von euch braucht

| Datei | Ort | Inhalt |
|---|---|---|
| `SharedDeck-Setup.exe` | `shareddeck/dist/` | App und SharedDeck-Modul, Installer mit Deinstallation |
| `SharedDeck-A32NX.zip` | `shareddeck/dist/` | der SharedDeck-A32NX (FBW-Fork) |
| Tailscale (empfohlen) | <https://tailscale.com> | beide im selben Tailnet |

Wichtig: **Beide müssen exakt denselben Build des Flugzeugs verwenden.** Die App prüft das beim Verbinden und lehnt ab, wenn die Builds verschieden sind. Gib deinem Freund deshalb genau diese beiden Dateien.

## 2. Installation (beide)

Die Schritt-für-Schritt-Anleitung für deinen Freund steht in [installation-co-pilot.md](installation-co-pilot.md). Für dich als Host ist es dasselbe:

1. MSFS schließen.
2. `SharedDeck-Setup.exe` starten. Den Community-Ordner findet es selbst. Die ZIP wird erkannt, wenn sie daneben liegt.
3. Installieren. Ein vorhandener FBW A32NX wird nach `SharedDeck-Backup` verschoben, nicht gelöscht.

Wer lieber per Kommandozeile installiert:

```bash
SharedDeck-Setup.exe /silent /aircraft:SharedDeck-A32NX.zip /log:setup.log
```

## 3. Netzwerk

- **Mit Tailscale** (am einfachsten): Beide installieren Tailscale und melden sich im selben Netz an. Der Co-Pilot verbindet sich später mit der Tailscale-IP des Hosts (`100.x.y.z`) oder dessen Gerätenamen.
- **Ohne Tailscale:** Der Host gibt im Router den TCP-Port **51530** auf seinen PC frei, der Co-Pilot nutzt die öffentliche IP des Hosts.
- Beim ersten „Als Host starten“ fragt die Windows-Firewall nach. Erlaubt den Zugriff, bei Tailscale „Private Netzwerke“.

## 4. Flug vorbereiten (beide)

1. Beide starten einen Flug mit dem A32NX **am selben Flughafen**. Wetter-Preset und Uhrzeit sollten gleich sein. Die Cockpit-Anzeigen kommen ohnehin vom Host, aber Optik und Wolken rechnet jeder Simulator selbst.
2. **Kapitän (Host)** sitzt links, **Co-Pilot (Client)** wechselt in den rechten Sitz (FO-Kameraposition).
3. `SharedDeck.App.exe` starten. Unter „Simulator“ muss **verbunden (…A32NX…)** stehen. Falls nicht: Ist das Flugzeug geladen und das Modul installiert?

## 5. Verbinden

1. **Host:** „Als Host starten“. Das Banner zeigt „Host bereit – warte auf Co-Pilot“.
2. **Co-Pilot:** Adresse des Hosts eintragen, „Verbinden“.
3. Erwartet:
   - Das Flugzeug des Co-Piloten springt an die Position des Hosts.
   - Das Cockpit des Co-Piloten übernimmt den Zustand des Hosts: Schalter, Displays, Treibstoff, Flugplan, MCDU.
   - Nach wenigen Sekunden wird das Banner grün: **Synchron**.

## 6. Der erste Test: Abstimmungsmodus

Beim ersten Mal werden mit hoher Wahrscheinlichkeit Variablen auftauchen, die der Simulator des Co-Piloten selbst beschreibt, etwa reine Anzeige-Zustände. Im strikten Modus würde jede davon sofort einen Not-Halt auslösen.

1. **Host:** Vor „Als Host starten“ den Haken **Strikter Modus** entfernen.
2. Gemeinsam 20–30 Minuten alle Phasen durchspielen: Cold & Dark, Boarding/Betankung über das flyPad des Hosts, Triebwerksstart, Taxi, Start, Steigflug mit Autopilot, Anflug.
3. In der App des Co-Piloten zeigen sich zwei Listen:
   - **Abweichungen:** Variablen, deren Wert nach Korrektur nicht hält. Das deutet auf einen echten Fehler hin, bitte notieren.
   - **Auf dem Co-Pilot-PC lokal beschrieben:** Variablen, die dort ein Programmteil laufend schreibt.
4. Der **Host** prüft die Liste der lokal beschriebenen Variablen und übernimmt mit **„Lokale Variablen freigeben“** nur die, die wirklich pro Pilot sein dürfen, etwa Anzeige- oder Kameradetails.
5. Zusätzlich kann der Co-Pilot mit **„A:Var-Audit“** weitere Simulator-Variablen vergleichen, z. B. Türen, Fahrwerk und Licht. Was dort abweicht, fehlt noch in der `avars`-Liste der Konfiguration.
6. Danach den Haken **Strikter Modus** wieder setzen. Ab jetzt pausiert jede nicht freigegebene Abweichung beide Simulatoren.

Bitte schickt mir nach dem Test:
- das Protokoll aus der App
- beide Listen
- das Audit-Ergebnis

Daraus wird die Konfiguration fertig abgestimmt.

## 7. Bedienung im gemeinsamen Flug

| Was | Wie |
|---|---|
| Schalter, Knöpfe, Displays, ECAM, FCU, EFIS | Beide bedienen alles. Eingaben des Co-Piloten gehen an den Host und erscheinen nach einer Hin-und-Rück-Laufzeit (siehe „Latenz“ in der App). |
| **Sidesticks** | Kapitän: eigener Stick wie gewohnt. Co-Pilot: seine Stick-Achsen steuern den **rechten Sidestick**. Gleichzeitige Eingaben werden wie im echten A320 addiert. Die **Priority-Taste** (rotes Knöpfchen am Stick) übergibt die Kontrolle. |
| **Schubhebel** | Soft-Takeover: Der Hebel des Kapitäns hat Vorrang. Der Co-Pilot übernimmt, sobald sein Hebel die aktuelle Stellung erreicht (±4 %). Bewegt danach der Kapitän seinen Hebel, ist er wieder dran. Wer gerade steuert, zeigt die App unter „Schubhebel steuert“. |
| Pedale, Bremsen | Wer zuletzt bewegt, bestimmt (die Pedale sind auch im echten Flugzeug gekoppelt). |
| MCDU | Beide MCDUs zeigen dasselbe (wie bisher im A32NX). Die Tasten des Co-Piloten werden beim Host ausgeführt, auch die Eingabe über die PC-Tastatur. |
| flyPad | Jeder hat sein eigenes Tablet. Betankung, Beladung, Türen, Bodendienste, SimBrief-Import und Failures funktionieren von beiden Tablets. Helligkeit, Einheiten und Ein/Aus bleiben pro Pilot. |
| Uhrzeit | Wird automatisch an den Host angeglichen (minutengenau). |
| Pause | Nur der Host pausiert, der Co-Pilot folgt automatisch. |

## 8. Not-Halt und Verbindungsabbruch

| Ereignis | Was passiert | Was tun |
|---|---|---|
| Abweichung (strikter Modus) | Beide Simulatoren pausieren, das Banner wird rot und nennt die Variable | „Neu synchronisieren und fortsetzen“ |
| Verbindung weg | Der Co-Pilot pausiert (und auf Wunsch auch der Host) | Co-Pilot klickt erneut „Verbinden“ |
| Eigener Simulator / Flugzeug neu geladen | Not-Halt | Flugzeug laden, dann fortsetzen |
| „Trennen“ beim Co-Piloten | Sein Flugzeug löst sich und fliegt ab dem letzten gespiegelten Zustand selbst weiter | – |

## 9. Bekannte Grenzen dieses Entwurfs

- **Nicht im Simulator getestet.** Erwartet beim ersten Test Fehler. Das Protokoll in der App hilft beim Finden.
- **Wetter** kann ein Add-on in MSFS nicht setzen. SharedDeck vergleicht Wind, Temperatur, Luftdruck, Sicht und Niederschlag und zeigt Abweichungen unter „Abweichungen“ als **Hinweis** an, ohne Not-Halt. Die Anzeigen im Cockpit stimmen trotzdem, weil sie vom Host kommen. Nur Wolken und Optik können sich unterscheiden. Am besten beide „Live-Wetter“ wählen.
- Eingaben des Co-Piloten haben die Netzwerk-Laufzeit als Verzögerung (bei 50 ms RTT kaum spürbar).
- Der Installer ist nicht digital signiert, deshalb warnt Windows SmartScreen beim ersten Start.
- Die ersten paar Werte einer laufend lokal beschriebenen Variable können an den Host gelangen, bevor sie als „lokal beschrieben“ erkannt wird. Nach dem Freigeben passiert das nicht mehr.
