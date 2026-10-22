# SharedDeck installieren – Anleitung für den Co-Piloten

Mit SharedDeck fliegt ihr zu zweit **ein** Flugzeug, jeder an seinem eigenen PC in MSFS 2024. Dein Freund ist der **Host** und sitzt links als Kapitän. Du bist der **Co-Pilot** und sitzt rechts. Alles im Cockpit siehst du genau so wie er, und du kannst alles bedienen.

Die Installation dauert etwa 15 Minuten, den Download nicht mitgerechnet.

---

## Was du vom Host bekommst

Zwei Dateien, am besten zusammen im selben Ordner, z. B. in `Downloads\SharedDeck`:

| Datei | Größe | Inhalt |
|---|---|---|
| `SharedDeck-Setup.exe` | ca. 140 MB | die SharedDeck-App und das SharedDeck-Modul für MSFS |
| `SharedDeck-A32NX.zip` | einige GB | der A320neo von FlyByWire in der SharedDeck-Version |

> **Wichtig:** Nimm genau die ZIP-Datei, die dir der Host gibt, und keine andere A32NX-Version. Beide müssen exakt denselben Stand haben, sonst lehnt die App die Verbindung ab.

## Schritt 1: MSFS 2024 schließen

Der Simulator darf während der Installation nicht laufen.

## Schritt 2: Setup ausführen

1. Doppelklick auf **`SharedDeck-Setup.exe`**.
   - Falls Windows „Der Computer wurde durch Windows geschützt“ meldet: auf **Weitere Informationen** und dann **Trotzdem ausführen** klicken. Das Setup ist nicht digital signiert, deshalb fragt Windows nach.
2. Im Setup prüfen:
   - **Programmordner:** Den vorgeschlagenen Ordner kannst du einfach lassen.
   - **MSFS 2024 Community-Ordner:** Wird normalerweise automatisch gefunden. Falls das Feld leer ist, über **…** den Ordner `Community` auswählen.
     - Microsoft Store: meist `C:\Users\<Name>\AppData\Local\Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\Packages\Community`
     - Steam: der Ordner, den du bei der ersten Installation von MSFS 2024 als Paketordner gewählt hast, darin `Community`
     - Notfalls: In MSFS unter *Optionen → Allgemein → Entwickler* den Entwicklermodus einschalten. Danach zeigt *Werkzeuge → Virtuelles Dateisystem → Paketordner* den Pfad.
   - **SharedDeck-A32NX (optional):** Liegt die ZIP-Datei neben dem Setup, ist das Feld schon ausgefüllt. Sonst über **…** die `SharedDeck-A32NX.zip` auswählen.
3. **Installieren** klicken. Das Entpacken des Flugzeugs dauert ein paar Minuten.
4. Am Ende steht **Fertig.** im Fenster.

**Was das Setup tut:**
- Es installiert die SharedDeck-App und legt Verknüpfungen auf dem Desktop und im Startmenü an.
- Es kopiert das SharedDeck-Modul in deinen Community-Ordner.
- Es entpackt den SharedDeck-A32NX in deinen Community-Ordner. Hattest du schon einen FlyByWire A32NX, wird er **nicht gelöscht**, sondern in den Ordner `SharedDeck-Backup` neben dem Community-Ordner verschoben.

> **Achtung FBW Installer:** Aktualisiere den A32NX nicht über den FlyByWire Installer, solange du SharedDeck nutzt. Der Installer würde die SharedDeck-Version überschreiben.

## Schritt 3: Tailscale einrichten

Tailscale verbindet eure beiden PCs, als stünden sie im selben Heimnetz. Ihr müsst dafür nichts am Router einstellen.

1. <https://tailscale.com/download> öffnen, Tailscale für Windows herunterladen und installieren.
2. Anmelden. Dein Freund schickt dir dafür eine Einladung in sein Tailscale-Netz, oder ihr meldet euch mit demselben Konto an. Klärt das vorher kurz miteinander.
3. Im Tailscale-Symbol in der Taskleiste siehst du jetzt den PC deines Freundes mit einer Adresse wie `100.101.102.103`. **Diese Adresse brauchst du gleich.**

## Schritt 4: Erster Start

1. **MSFS 2024 starten.**
2. Einen Flug mit dem **A320neo (FlyByWire)** am selben Flughafen wie dein Freund starten.
   - Wetter und Uhrzeit möglichst gleich einstellen, z. B. beide „Live-Wetter“ und „Live-Zeit“. Die Uhrzeit gleicht SharedDeck automatisch an. Das Wetter rechnet jeder Simulator selbst, die Anzeigen im Cockpit kommen aber immer vom Host.
3. In der Cockpit-Ansicht auf den **rechten Sitz** wechseln (Kameraposition des First Officers).
4. **SharedDeck** über die Desktop-Verknüpfung starten. Unter „Simulator“ muss nach ein paar Sekunden **verbunden** stehen.
   - Falls nicht: Läuft der Flug schon? Ist der A320neo von FlyByWire geladen? Notfalls MSFS neu starten.

## Schritt 5: Verbinden

1. Dein Freund klickt in seiner App auf **„Als Host starten“**.
2. Du trägst unter „Co-Pilot“ seine Tailscale-Adresse ein und klickst **„Verbinden“**.
3. Dein Flugzeug springt an seine Position, und dein Cockpit übernimmt seinen Zustand. Nach ein paar Sekunden wird das Banner grün: **„Synchron – du bist Co-Pilot“**.

## So fliegt ihr zusammen

| Was | Wie |
|---|---|
| Schalter, Knöpfe, Displays | Alles bedienbar wie gewohnt. Deine Eingaben laufen über den PC des Hosts und erscheinen nach einem kurzen Moment. |
| **Sidestick** | Dein Joystick steuert den **rechten Sidestick**. Bewegt ihr beide gleichzeitig, werden die Eingaben addiert und es kommt „DUAL INPUT“, wie im echten A320. Mit der **Priority-Taste** am Stick übernimmst du die Steuerung. |
| **Schubhebel** | Der Hebel des Kapitäns hat Vorrang. Willst du übernehmen, schiebe deinen Hebel auf die **aktuelle Stellung**. Ab dann steuerst du. In der App steht unter „Schubhebel steuert“, wer gerade dran ist. |
| MCDU | Klicken oder per Tastatur tippen: Klicke dazu auf das MCDU-Display (Tastatureingabe muss im flyPad unter *Einstellungen → Realism* aktiviert sein). |
| flyPad | Du hast dein eigenes Tablet. Betankung, Beladung, Türen und Failures funktionieren auch bei dir. |
| Pause | Nur der Host pausiert. Dein Simulator folgt automatisch. |

## Wenn etwas schiefgeht

| Was du siehst | Was tun |
|---|---|
| Banner **rot: NOT-HALT** | SharedDeck hat einen Unterschied zwischen euren Simulatoren gefunden und beide pausiert. Einer von euch klickt **„Neu synchronisieren und fortsetzen“**. Tritt es wiederholt auf: Text im Banner und Protokoll an den Host schicken. |
| **„Abgelehnt: unterschiedliche Flugzeug-Builds“** | Du hast eine andere A32NX-Version als der Host. Setup mit der ZIP vom Host noch einmal ausführen. |
| **„Verbindung fehlgeschlagen“** | Läuft Tailscale bei beiden? Ist die Adresse richtig? Hat der Host „Als Host starten“ geklickt? Beim Host fragt die Windows-Firewall beim ersten Mal, ob SharedDeck Verbindungen annehmen darf; dort muss er **Zulassen** wählen. |
| **„Verbindung verloren – pausiert“** | Einfach erneut **„Verbinden“** klicken. |
| Weiterfliegen ohne den Host | **„Trennen“** klicken. Dein Flugzeug fliegt ab dem letzten gemeinsamen Zustand allein weiter. |

## Deinstallieren

*Windows-Einstellungen → Apps → Installierte Apps → „SharedDeck – Shared Cockpit für MSFS 2024“ → Deinstallieren.*

Dabei werden App, Verknüpfungen und das SharedDeck-Modul entfernt. Das Flugzeug bleibt installiert. Deinen ursprünglichen A32NX findest du im Ordner `SharedDeck-Backup` neben dem Community-Ordner. Zum Zurückwechseln: SharedDeck-A32NX aus dem Community-Ordner löschen und den gesicherten Ordner wieder hineinschieben.
