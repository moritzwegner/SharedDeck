# SharedDeck-Änderungen am FlyByWire A32NX

Die Patches `0001-…` bis `0004-…` enthalten alle Änderungen am FlyByWire A32NX (Client-Modus der Systeme und Display-Rechner, F/O-Sidestick, Flugplan-/MCDU-Spiegelung, RMP, Uhr, Failures, SharedDeck-Bridge).

Basis ist [flybywiresim/aircraft](https://github.com/flybywiresim/aircraft) Commit `76db0aef132fd9698fd4013e2d7239c517dd284e`. Die Skripte von SharedDeck erwarten den Fork unter `vendor/flybywire-aircraft`.

```bash
git clone https://github.com/flybywiresim/aircraft.git vendor/flybywire-aircraft
cd vendor/flybywire-aircraft
git checkout -b shareddeck/client-mode 76db0aef132fd9698fd4013e2d7239c517dd284e
git am ../../fbw-patch/*.patch
git submodule update --init --depth 1 large-files
./scripts/dev-env/run.sh ./scripts/setup.sh
./scripts/dev-env/run.sh bash -c 'FBW_TYPECHECK=1 ./scripts/build.sh -r a32nx -j 2 --no-tty'
```

Ergebnis: `fbw-a32nx/out/flybywire-aircraft-a320-neo` und `…-lock-highlight`. Das Paket für die Weitergabe baut `shareddeck/installer/build-installer.ps1 -Aircraft`.

FlyByWire steht unter GPL-3.0, die Patches damit ebenfalls.
