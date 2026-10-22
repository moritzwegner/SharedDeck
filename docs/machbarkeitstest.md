# Machbarkeitstest: Client-Modus im FBW A32NX

**Ziel:** Mit `L:SHAREDDECK_ROLE = 1` hört die gesamte WASM-Berechnung des A32NX auf zu rechnen. Die Displays laufen trotzdem fehlerfrei weiter. Mit `0` läuft alles ohne Sprünge oder Fehlermeldungen wieder an.

Getestet wird auf **einem PC**, ohne Netzwerk. Die Physik läuft dabei noch weiter, denn das Einfrieren der Physik übernimmt später die SharedDeck-App. Deshalb findet der Test **am Boden** statt.

---

## 1. Was geändert wurde

FBW-Fork: `vendor/flybywire-aircraft`, Branch `shareddeck/client-mode` (Basis: Commit `76db0ae`). Die Änderungen sind noch nicht committet.

| Schicht | Datei | Verhalten bei `L:SHAREDDECK_ROLE = 1` |
|---|---|---|
| Rust-Systeme (`systems.wasm`) | `fbw-common/src/wasm/systems/systems_wasm/src/lib.rs` | `simulation.tick` wird übersprungen. Die vergangene Zeit wird verworfen, damit beim Zurückschalten kein riesiger Zeitschritt entsteht. |
| C++-Framework (`fadec-a32nx.wasm`, `extra-backend-a32nx.wasm`) | `fbw-common/src/wasm/cpp-msfs-framework/MsfsHandler/MsfsHandler.cpp/.h` | Alle Module werden übersprungen: FADEC, Pushback, Presets. Der Zeitstempel läuft weiter. |
| Fly-by-Wire-Gauge (`fbw.wasm`) | `fbw-a32nx/src/wasm/fbw_a320/src/FlyByWireInterface.cpp/.h`, `interface/SimConnectInterface.cpp/.h` | `update()` bricht sofort ab. Anstehende SimConnect-Nachrichten werden verworfen (neue Funktion `discardData()`), damit beim Zurückschalten keine veralteten Eingaben ankommen. |

Die Variable heißt bewusst `SHAREDDECK_ROLE` **ohne** `A32NX_`-Präfix. So funktioniert sie später auch für den A380X.

## 2. Gebaute Dateien

Ausgabeordner: `vendor/flybywire-aircraft/fbw-a32nx/out/flybywire-aircraft-a320-neo/SimObjects/AirPlanes/FlyByWire_A320_NEO/panel/`

- `systems.wasm`
- `fbw.wasm`
- `fadec-a32nx.wasm`
- `extra-backend-a32nx.wasm`

Neu bauen (Docker Desktop muss laufen), aus `vendor/flybywire-aircraft` heraus:

```bash
./scripts/dev-env/run.sh bash -c "npm run build-a32nx:systems && npm run build-a32nx:fbw && npm run build:cpp-wasm-cmake"
```

## 3. Installation

1. Mit dem **FBW Installer** den A32NX in der Version **Development** installieren. Die gebauten WASM-Dateien stammen vom Development-Stand vom 9. Oktober 2026. Mit Stable passen sie wahrscheinlich nicht zu JS-Avionik und Konfiguration.
2. Im Community-Ordner `flybywire-aircraft-a320-neo\SimObjects\AirPlanes\FlyByWire_A320_NEO\panel\` die vier Originaldateien **sichern**, z. B. in einen Ordner `panel_original`.
3. Die vier gebauten Dateien dorthin kopieren.
4. MSFS 2024 komplett neu starten. WASM-Module werden nur beim Start geladen.

Falls der A32NX danach schon im Normalmodus (`ROLE = 0`) Fehler zeigt, passt der installierte Development-Stand nicht zum Build. Dann wäre ein Vollbuild des ganzen Flugzeugs aus dem Fork nötig.

## 4. L:SHAREDDECK_ROLE setzen

MSFS-Entwicklermodus aktivieren. Dann eine der beiden Möglichkeiten:

- **Coherent GT Debugger** (sicherer Weg): mit einem Instrument verbinden (z. B. PFD) und in der Konsole ausführen:
  ```js
  SimVar.SetSimVarValue('L:SHAREDDECK_ROLE', 'number', 1)   // Client-Modus an
  SimVar.SetSimVarValue('L:SHAREDDECK_ROLE', 'number', 0)   // wieder normal
  ```
- **Behaviors-Fenster:** Tools → Behaviors, dort RPN-Code ausführen: `1 (>L:SHAREDDECK_ROLE)` bzw. `0 (>L:SHAREDDECK_ROLE)`.

## 5. Testablauf

**Ausgangslage:** Am Gate, beide Triebwerke im Leerlauf, Parkbremse gesetzt. Am ECAM die SD-Seite FUEL aufrufen.

| # | Aktion | Erwartet | Ergebnis |
|---|---|---|---|
| 1 | Ausgangswerte notieren: FOB, FU (pro Triebwerk), N1, N2, EGT, FF, Hydraulikdrücke | – | |
| 2 | `ROLE = 1` setzen | Alle Displays bleiben an, keine roten Flags oder schwarzen Bildschirme | |
| 3 | 60 Sekunden warten | **FU bleibt stehen.** N1, N2, EGT und FF bleiben stehen (FADEC aus). | |
| 4 | Dabei FOB beobachten | Bleibt FOB stehen, verbraucht das native MSFS-Triebwerk keinen Treibstoff. **Sinkt FOB, ist die offene Frage aus der Analyse damit beantwortet: Das native Modell verbraucht weiter.** | |
| 5 | Sidestick bewegen, SD-Seite F/CTL beobachten | Ruder bewegen sich nicht (ELAC/SEC aus) | |
| 6 | Hydraulikpumpe am Overhead ausschalten | Druck auf der SD-Seite HYD ändert sich **nicht** (Rust-Systeme aus) | |
| 7 | Konsole prüfen: Coherent-Debugger und MSFS-Konsole (Dev Mode → Console) | Keine sich wiederholenden Fehler | |
| 8 | `ROLE = 0` setzen | Alles läuft weiter: FU steigt wieder, Hydraulik reagiert auf den Schalter aus Schritt 6 | |
| 9 | 2 Minuten beobachten | Keine falschen ECAM-Warnungen, keine Sprünge bei Treibstoff oder Triebwerkswerten, keine ausgefallenen Systeme | |

Erst wenn der Bodentest sauber durchläuft, folgt optional ein kurzer Test in der Luft mit Autopilot und höchstens 5 Sekunden `ROLE = 1`. Weil die Physik dabei weiterläuft, gibt dieser Test nur Hinweise und ist **kein** Bestehen-Kriterium.

## 6. Was bewusst noch nicht abgedeckt ist

- **JS-Avionik** (FMS, FWC, Stromversorgung, ATSU) rechnet weiter. Das ist Schicht 4 und der nächste Schritt.
- **Physik des Clients einfrieren** und Werte vom Host einspielen: Aufgabe der SharedDeck-App.
- Gesperrte **Tastatur- und Hardware-Eingaben** im Client-Modus werden verworfen. Weiterleiten an den Host kommt mit der App.
