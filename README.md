# SharedDeck

Shared Cockpit für **Microsoft Flight Simulator 2024**: Zwei Piloten fliegen an zwei PCs gemeinsam **ein** Flugzeug, den FlyByWire A32NX. Beide können alles bedienen, und alle Werte (Treibstoff, Geschwindigkeit, Anzeigen …) sind auf beiden PCs identisch.

Der Host-Simulator rechnet das Flugzeug, der Simulator des Co-Piloten spiegelt es nur und schickt die Eingaben seines Piloten zurück. Jede Abweichung wird erkannt. Im strikten Modus pausiert sie beide Simulatoren.

> **Stand:** Erster Entwurf. Gebaut und automatisch getestet, aber **noch nicht im Simulator getestet**.

## Inhalt

| Ordner | Inhalt |
|---|---|
| [`shareddeck/`](shareddeck/README.md) | WASM-Modul, Desktop-App, Installer, Build-Skripte – technischer Überblick in der README dort |
| [`fbw-patch/`](fbw-patch/README.md) | Änderungen am FlyByWire A32NX als Patch, mit Anleitung |
| [`docs/`](docs) | Konzept-Analyse, Machbarkeitstest, Anleitungen |

## Anleitungen

- [Gemeinsam fliegen – Anleitung für Host und Co-Pilot](docs/anleitung-gemeinsamer-flug.md)
- [Installation für den Co-Piloten](docs/installation-co-pilot.md)
- [Analyse des FBW A32NX](docs/fbw-a32nx-analyse.md)
- [Machbarkeitstest Client-Modus](docs/machbarkeitstest.md)
