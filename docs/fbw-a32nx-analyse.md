# SharedDeck: Analyse des FBW A32NX für den Client-Modus

Untersuchter Stand: `flybywiresim/aircraft`, Commit `76db0ae` (9. Okt. 2026), flach geklont nach `vendor/flybywire-aircraft`.

Ziel: herausfinden, an welchen Stellen ein Fork die gesamte Berechnung auf dem Client abschalten muss, damit nur noch der Host rechnet (siehe Konzept, Regel 1 und 2).

---

## 1. Die Rechenschichten des A32NX und ihre Schalter

Der A32NX rechnet in **vier getrennten Schichten**. Jede hat genau eine zentrale Update-Funktion. Dort kommt jeweils die Abfrage `L:SHAREDDECK_ROLE == 1` (Client) hinein.

| # | Schicht | Was dort berechnet wird | Zentrale Update-Funktion | Eingriff |
|---|---|---|---|---|
| 1 | **Rust-Systeme** (`systems.wasm`) | Elektrik, Hydraulik, Pneumatik, **ADIRS (Airspeed, Höhe, Lage)**, Bremsen, Fahrwerk, Klappen, Payload, Treibstoffpumpen | `fbw-common/src/wasm/systems/systems_wasm/src/lib.rs:235`: `MSFSEvent::PreDraw` ruft `pre_tick`, `simulation.tick` und `post_tick` auf | **Trivial.** Es gibt schon eine Pause-Abfrage (`if !self.time.is_pausing()`). Die Client-Bedingung kommt dazu. |
| 2 | **C++-Framework-Module** (FADEC, Extra-Backend) | Triebwerke (N1, N2, EGT, FF), **Treibstoffverbrauch**, Pushback, Aircraft Presets, Lighting Presets | `fbw-common/src/wasm/cpp-msfs-framework/MsfsHandler/MsfsHandler.cpp:123`: Pause-Abfrage vor allen Modulen | **Trivial.** Eine Abfrage im Framework deckt alle Module beider Gauges ab. |
| 3 | **Fly-by-Wire-Gauge** (`fbw_a320`) | ELAC, SEC, FAC, FCDC, FCU, Autopilot/Autothrust, RA, LGCIU, SFCC, ILS, TCAS | `fbw-a32nx/src/wasm/fbw_a320/src/FlyByWireInterface.cpp:61`. Die vorhandene Pause/Slew-Abfrage steht erst bei `:82–88`. | **Einfach.** Die Abfrage muss **ganz an den Anfang**, weil `readDataAndLocalVariables` und `handleFcuInitialization` vorher schon schreiben. |
| 4 | **JS-Avionik** | FMS, Guidance, Flight Phase (MCDU), FWC/ECAM-Logik, Stromversorgung, ATSU, Key-Interceptor | `A32NX_FMCMainDisplay.ts:783` (`onUpdate`), dazu `:862` (`onUpdateHiFreq`), außerdem `systems-host/systems/*` und `extras-host/modules/*` | **Mittel.** Mehrere Instrumente, jedes braucht eine eigene Abfrage. Die Anzeigen (PFD, ND, EWD, SD) bleiben aktiv. |

**Wichtig für die Anzeigen:** Die ADIRS-Ausgaben (z. B. `A32NX_ADIRS_ADR_1_COMPUTED_AIRSPEED`) erzeugt die **Rust-Schicht** (`fbw-common/src/wasm/systems/systems/src/navigation/adirs.rs:1100`). Ist Rust auf dem Client aus und kommen diese L:Vars vom Host, zeigt der PFD des Clients **genau die Geschwindigkeit des Hosts**. Das gilt unabhängig von Wetter und Physik auf dem Client.

---

## 2. Warum Treibstoff bei YourControls abweicht (im Code bestätigt)

Im FADEC (`fbw-a32nx/src/wasm/fadec_a32nx/src/Fadec/EngineControlA32NX.cpp`) gibt es drei Gründe:

1. **Verbrauch auf beiden PCs:** `updateFuel()` (`:136`) berechnet den Verbrauch selbst und schreibt die Tankmengen in den Sim (`:841`, `:846`, `:1011`). Läuft das auf beiden PCs mit unterschiedlicher Framerate, gehen die Werte zwangsläufig auseinander.
2. **Gespeicherte Tankfüllung pro ATC-ID:** Beim Start lädt der FADEC eine gespeicherte Tankfüllung aus einer INI-Datei, abhängig von der ATC-ID bzw. Livery (`loadFuelConfigIfPossible()`, `:156`, und `initializeFuelTanks()`, `:260–301`). Hat der Client eine andere Livery oder einen alten Speicherstand, **startet er mit einer anderen Tankfüllung**.
3. **Client überschreibt die Datei:** Der FADEC speichert die Tankfüllung regelmäßig in die INI (`:1020–1028`). Ein Client würde damit seine eigene, falsche Datei schreiben.

Der Client-Modus unterbindet alle drei Punkte, weil das ganze FADEC-Modul auf dem Client nicht mehr läuft (Schicht 2).

**Noch offen:** Ob das **native MSFS-Triebwerksmodell** auf dem Client trotz eingefrorener Physik weiter Treibstoff verbraucht. Falls ja, braucht der Client z. B. „Unlimited Fuel“, oder der Host überschreibt die Tankwerte in jedem Frame. Der Zustands-Hash würde eine Abweichung in jedem Fall sofort melden.

---

## 3. Flugplan und FMS: FBW hat schon ein Sync-Protokoll

Der Flugplan steckt nicht in L:Vars, sondern im JS-Speicher. FBW synchronisiert ihn aber bereits zwischen mehreren Instrumenten:

- **Master/Client-Protokoll:** `fbw-a32nx/src/systems/fmgc/src/flightplanning/FlightPlanManager.ts` mit
  - `flightPlanManager.syncRequest` / `syncResponse`: kompletter serialisierter Flugplan (`:80–110`)
  - Änderungen als Events mit `syncClientID` (`create`, `delete`, `swap`, `copy`, Edits, …) über `SYNC_*`-Topics (`:389–394`)
  - Erneute Sync-Anfrage nach dem Start (`:182`)
- **RPC:** `FlightPlanRpcServer` wird in `A32NX_FMCMainDisplay.ts:143` erzeugt. `FlightPlanRpcClient` schickt Befehle an den `FlightPlanService` des Masters.

**Folgerung für SharedDeck:** Dieses Protokoll wird **über das Netzwerk verlängert**:
- Der FlightPlanManager des Clients ist nie Master.
- Eingaben am Client-MCDU gehen als RPC-Befehle an den Host.
- Die `SYNC_*`-Events des Hosts kommen beim Client an.
- Zusätzlich fließt der serialisierte Flugplan (`syncResponse`) in den Zustands-Hash.

**Noch offen:** Wie der EventBus Daten zwischen Instrumenten überträgt, steckt im Submodul `fbw-common/msfs-avionics-mirror` (`@microsoft/msfs-sdk ^2.3.0`). Das Submodul ist im flachen Klon leer. Die sichere Variante ist ein **eigenes kleines JS-Bridge-Instrument** von SharedDeck: Es abonniert die Topics im Sim und reicht sie per CommBus an das SharedDeck-WASM-Modul weiter.

---

## 4. Sidesticks: Zwei-Piloten-Logik ist schon da, aber nicht angeschlossen

Wichtigster Fund für „wie im echten Leben“:

- Die ELAC- und SEC-Modelle haben **bereits getrennte Eingänge** für Captain und FO sowie die komplette Priority-Logik:
  - `ElacComputer_types.h:111–112`: `capt_/fo_priority_takeover_pressed`
  - `:300–303`: `left_/right_sidestick_*_command`
  - `:462–482`: `has_priority_in_pitch/roll`, `left_/right_sidestick_disabled`, `left_sidestick_priority_locked`
- Die Priority-Tasten sind schon angebunden: `A32NX_PRIORITY_TAKEOVER:1/:2` (`FlyByWireInterface.cpp:714–715`).
- **Aber:** Der FO-Stick ist fest auf null verdrahtet:
  - `FlyByWireInterface.cpp:1403–1406` (ELAC): `fo_pitch_stick_pos = 0`, `fo_roll_stick_pos = 0`
  - `:1560–1579` (SEC): ebenso
  - Es gibt nur eine Stick-L:Var: `A32NX_SIDESTICK_POSITION_X/Y` (`:362`).

**Patch:** Neue L:Vars `A32NX_SIDESTICK_FO_POSITION_X/Y` anlegen und in die `fo_*_stick_pos`-Eingänge einspeisen. Der Client schreibt seinen Stick dorthin. Addition, Priority und Sidestick-Abschaltung kommen dann aus dem **originalen FBW-Modell**. Ob auch Callout und Lampen („DUAL INPUT“, Priority-Pfeile) schon daran hängen, ist im Test zu prüfen.

Dieser Patch nützt auch FBW ohne SharedDeck, z. B. für Home-Cockpits mit zwei Sticks. Er ist ein guter erster Upstream-Beitrag.

---

## 5. Eingaben des Clients erfassen

- `fbw-a32nx/docs/a32nx-input-events.md` deckt bisher **nur ECP und Overhead-Lights** ab. Input Events reichen für den A32NX also **nicht**. Die meisten Bedienelemente schreiben L:Vars oder lösen H:Events aus.
- **Lösung:** Im Client-Modus rechnet auf dem Client nichts mehr. Ändert sich dort eine L:Var, ohne dass ein Host-Snapshot sie gesetzt hat, **kann das nur eine Pilotenaktion sein** (oder rein lokale Anzeigelogik).
  1. Das SharedDeck-WASM-Modul erkennt die Änderung.
  2. Es schickt sie als Befehl an den Host.
  3. Es setzt den Wert lokal zurück. Der bestätigte Wert kommt mit dem nächsten Snapshot.
- Rein lokale Anzeigelogik (z. B. UI-Zustände einzelner Displays) wird bei den ersten Tests gemessen und kommt auf die Ausnahmeliste.
- **Achtung bei Schicht 2:** `Pushback_A32NX` bewegt das Flugzeug, `AircraftPresets` führt ganze Schaltfolgen aus. Beides darf **nur auf dem Host** laufen. Der Client schickt nur den Befehl.

---

## 6. Patch-Liste für den Fork

| Patch | Datei(en) | Aufwand |
|---|---|---|
| Client-Abfrage Rust-Systeme | `systems_wasm/src/lib.rs` | klein |
| Client-Abfrage C++-Framework (FADEC, Pushback, Presets) | `cpp-msfs-framework/MsfsHandler/MsfsHandler.cpp` | klein |
| Client-Abfrage Fly-by-Wire-Gauge | `fbw_a320/src/FlyByWireInterface.cpp` | klein |
| Tank-INI auf dem Client weder laden noch speichern | durch die Abfrage im Framework bereits abgedeckt | – |
| FO-Sidestick-Eingänge | `FlyByWireInterface.cpp`, neue L:Vars | klein bis mittel |
| Client-Abfrage JS (FMS, FWC, Stromversorgung, ATSU, Key-Interceptor) | `A32NX_FMCMainDisplay.ts`, `systems-host`, `extras-host` | mittel |
| Netzwerk-Brücke für Flugplan-Events und RPC | SharedDeck-JS-Bridge und kleine Anpassungen in `FlightPlanManager.ts` | mittel bis groß |

Die drei Abfragen in den WASM-Schichten (1 bis 3) sind **wenige Zeilen** und an genau bekannten Stellen. Der größte Aufwand und das größte Risiko liegen in der JS-Avionik.

---

## 7. Nächste Schritte

1. **FBW lokal bauen.** Gebaut wird mit pnpm, Rust (siehe `rust-toolchain.toml`) und dem WASM-Toolchain des MSFS SDK. Die genauen Schritte stehen in der README des Repos.
2. **Machbarkeitstest auf einem PC:**
   - Die drei WASM-Abfragen einbauen.
   - Im Flug `L:SHAREDDECK_ROLE = 1` setzen.
   - Erwartet: Alle Systeme frieren auf dem letzten Stand ein, die Displays laufen weiter, keine Fehler in der Konsole.
   - Das ist der entscheidende Test für das ganze Konzept.
3. **Variablen-Inventar:**
   - Ein kleines WASM-Tool listet zur Laufzeit **alle** L:Vars des A32NX auf. `docs/a320-simvars.md` dokumentiert nur 655, tatsächlich sind es deutlich mehr.
   - Dabei wird gemessen, welche sich im Client-Modus noch ändern. Das ist die Grundlage für die Ausnahmeliste.
4. FO-Sidestick-Patch einbauen und das Dual-Input-Verhalten testen.
