# SharedDeck – technischer Überblick

Shared Cockpit für den FlyByWire A32NX in MSFS 2024. Ein Simulator (Host) berechnet das komplette Flugzeug, der zweite (Client) spiegelt es und schickt nur die Eingaben seines Piloten zurück.

```
 PC Host (Kapitän)                                         PC Client (Co-Pilot)
 ┌───────────────────────────────┐                         ┌───────────────────────────────┐
 │ MSFS 2024                      │                         │ MSFS 2024                      │
 │  FBW A32NX (Fork) rechnet alles│                         │  FBW A32NX (Fork) Client-Modus │
 │  shareddeck-module.wasm ◄─┐    │                         │  shareddeck-module.wasm ◄─┐    │
 │  SharedDeck-Bridge (JS) ◄─┘    │                         │  SharedDeck-Bridge (JS) ◄─┘    │
 └──────────────┬────────────────┘                         └──────────────┬────────────────┘
                │ WebSocket 127.0.0.1:8391                                 │
 ┌──────────────▼────────────────┐   TCP 51530, komprimiert  ┌──────────────▼────────────────┐
 │ SharedDeck.App.exe (Host)     │ ◄───────────────────────► │ SharedDeck.App.exe (Client)   │
 └───────────────────────────────┘                           └───────────────────────────────┘
```

## Komponenten

| Teil | Ort | Aufgabe |
|---|---|---|
| WASM-Modul | `wasm/` → `package/shareddeck-module` | Standalone-Modul. Findet alle L:Vars, sendet geänderte Werte als nummerierte Frames, setzt sie beim Client, friert die Physik des Clients ein und setzt die Position vom Host, erkennt Eingaben (L:Var-Änderungen, Key-Events, FO-Sidestick), prüft den Zustand und pausiert. |
| JS-Bridge | FBW-Fork `extras-host/modules/shareddeck` | WebSocket zur App, CommBus zum Modul, spiegelt die EventBus-Kanäle (`wt.eb.evt`, `EB_EVENTS`) und leitet H-Events weiter. |
| FBW-Fork | `vendor/flybywire-aircraft`, Branch `shareddeck/client-mode` | Client-Modus (`L:SHAREDDECK_ROLE = 1`) in allen rechnenden Schichten, FO-Sidestick-Eingänge, Flugplan-Übernahme vom Host, MCDU-Spiegelung. |
| App | `app/` | Verbindung Host ↔ Client, Handshake (gleicher Build), Weiterleitung, Not-Halt, Oberfläche, A:Var-Audit. |
| Installer | `app/SharedDeck.Setup`, `installer/` → `dist/SharedDeck-Setup.exe` | Installiert App, Modul und optional das Flugzeug aus der ZIP (sichert einen vorhandenen A32NX), Verknüpfungen, Eintrag unter „Apps“, Deinstallation. `/silent` für die unbeaufsichtigte Installation. |

## Wie der Sync garantiert wird

1. **Nur der Host rechnet.** Rust-Systeme, FADEC/Extra-Backend, Fly-by-Wire/Autopilot, systems-host (FWC, ECAM-Logik, Strom, ATSU), FMS/MCDU und TCAS laufen auf dem Client nicht.
2. **Alles wird übertragen.** Alle L:Vars werden übertragen, ausgenommen ist nur eine explizite Sperrliste (`lvarDeny`). Dazu kommen die konfigurierten A:Vars (Treibstoff, Payload, Licht, Radios, …), der Körperzustand (Position, Lage, Geschwindigkeiten) und die JS-Daten über den EventBus.
3. **Reihenfolge und Vollständigkeit.** Frames sind nummeriert und laufen über TCP. Fehlt ein Frame, fordert der Client einen kompletten Keyframe an.
4. **Doppelte Prüfung (Client):**
   - Prüfsumme über alle Werte (Host-Hash ≙ Hash der empfangenen Werte), etwa jede Sekunde
   - Vergleich der Werte im Sim mit den empfangenen Host-Werten, jede Sekunde
5. **Not-Halt.** Im strikten Modus pausiert jede Abweichung beide Simulatoren, die nicht ausdrücklich als lokal freigegeben ist (`localAllow`). Ebenso eine Prüfsumme, die nach drei Neusynchronisierungen noch abweicht, ein Verbindungsabbruch und ausbleibende Frames (> 3 s).

## Bauen

Voraussetzungen: Docker Desktop (läuft), .NET 8 SDK, Node.js.

```bash
# WASM-Modul (im FBW-Docker-Image mit MSFS SDK 0.24.2)
bash wasm/build.sh
node tools/make-layout.js package/shareddeck-module

# App
dotnet publish app/SharedDeck.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/SharedDeck

# Tests der App und des Installers
dotnet test app/SharedDeck.Tests

# Installer (dist/SharedDeck-Setup.exe), mit -Aircraft zusätzlich dist/SharedDeck-A32NX.zip
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1 -Aircraft

# FBW-Fork (aus vendor/flybywire-aircraft)
./scripts/dev-env/run.sh bash -c 'FBW_TYPECHECK=1 ./scripts/build.sh -r a32nx'
```

## Konfiguration

`%APPDATA%\SharedDeck\shareddeck.json` (wird beim ersten Start angelegt). Maßgeblich ist immer die Datei des **Hosts**, sie wird beim Verbinden an den Client übertragen. Die Einträge sind in `SharedDeckConfig.cs` dokumentiert.

## Protokoll

Alle Nachrichten sind JSON-Objekte mit einem Kanal `ch`. Das WASM-Modul spricht über CommBus (`SHAREDDECK_FROM_WASM` / `SHAREDDECK_TO_WASM`).

**Nachrichten des Moduls:**
- `ready`, `log`, `stats`
- `names`: Index → Name der L:Vars
- `frame`: Base64-Deltas `l` (L:Vars) und `a` (A:Vars), Körperzustand `p`, `paused`, `ack`, optional Hash `h`
- `input`: L:Var-Schreibvorgänge und Key-Events des Co-Piloten
- `needKeyframe`, `verify`, `contested`, `audit`

Kodierung der Deltas: Varint-Index, danach der IEEE-754-Double in Little Endian. Der Hash ist FNV-1a 64 über (Index, Bits) in kanonischer Reihenfolge.
