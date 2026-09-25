# Max – Projektplan

> Max ist ein lokaler KI-Assistent fürs Terminal. Er läuft komplett auf dem eigenen Rechner, ohne Cloud und ohne API-Key, und kommt als eine einzige Datei für Windows (`max.exe`) und Linux (`max`).
> Phase 1 ist ein Chatbot. In Phase 2 wird Max zum Agenten, der Tools ausführen kann.

---

## 1. Entscheidungen

| Thema | Entscheidung |
|---|---|
| Sprache / Plattform | **C# mit .NET 10** |
| LLM-Engine | **LLamaSharp 0.27** (C#-Bindings für llama.cpp) mit den Backends **CPU und Vulkan**. Vulkan läuft auf NVIDIA, AMD und Intel mit dem normalen Treiber; CUDA (über 200 MB) bleibt vorerst draußen |
| Oberfläche | **Spectre.Console** im Stil von Claude Code: scrollender Chat, Eingabe unten, Farben, Markdown |
| Modellformat | **GGUF** (quantisiert, meist Q4_K_M) |
| Modellquelle | **Hugging Face**: direkte Download-Links, kein eigenes Hosting nötig |
| Zielsysteme | Windows x64 zuerst, danach Linux x64 |
| Auslieferung | Single-File-Publish; das Modell wird beim ersten Start heruntergeladen |
| Verteilung | Das Repo `PyritF/Max` ist **öffentlich**; Manifest und Releases werden ohne Token direkt von GitHub geladen (siehe Abschnitt 5a) |
| Updates | **Still im Hintergrund**: Download beim Start, beim nächsten Start ist die neue Version aktiv, ohne Rückfrage |
| Nutzung | privates Projekt, Lizenzfragen daher zweitrangig |

---

## 2. Grundprinzip: Max ist Max

Max soll wie ein eigenständiges Wesen wirken und nicht wie „noch eine KI mit Modell X dahinter“.

- **Kein Modellname und keine Stufe in der normalen Oberfläche.** Der Nutzer sieht nur „Max“.
- **Max entscheidet selbst**, welches Modell zur Hardware passt. Der Nutzer wählt nichts aus.
- **Technische Details gibt es nur unter `/debug`**: Modell, Stufe, Backend (CPU/CUDA/Vulkan), VRAM, Tokens pro Sekunde, Kontextgröße.
- **Neutrale Dateinamen auf der Festplatte.** Modelldateien heißen zum Beispiel `core.bin` und nicht `Qwen3-8B-Q4_K_M.gguf`.
- **Eigener System-Prompt** mit fester Identität (siehe Abschnitt 7). Max verrät nie, welches Modell oder welche Firma dahintersteckt.

---

## 3. Architektur

```
Max/
├── PLAN.md
├── manifest.json               ← aktuelle App-Version + Modell pro Stufe, wird von Max online gelesen
├── src/
│   └── Max/
│       ├── Max.csproj
│       ├── Program.cs          ← Einstiegspunkt: Setup → Chat-Loop
│       ├── Setup/              ← Erststart / „Installer“
│       │   ├── HardwareInfo.cs       (RAM, GPU, VRAM, CPU-Kerne)
│       │   ├── TierSelector.cs       (Hardware → Stufe)
│       │   ├── Manifest.cs           (manifest.json laden, eingebauter Fallback)
│       │   └── ModelDownloader.cs    (Download mit Fortschritt, Fortsetzen, Prüfsumme)
│       ├── Update/
│       │   ├── GitHubClient.cs       (Manifest und Release-Dateien laden)
│       │   ├── Updater.cs            (Hintergrund-Download von App und Modell)
│       │   └── UpdateApplier.cs      (bereitgelegtes Update beim Start aktivieren)
│       ├── Llm/
│       │   ├── LlmEngine.cs          (Modell laden, Backend wählen, Tokens streamen, Cache wiederverwenden)
│       │   ├── LlmBackend.cs         (System-Prompt + Verlauf → Prompt → Antwort)
│       │   ├── ChatTemplate.cs       (Verlauf → Prompt im Format des Modells, ChatML)
│       │   ├── ContextWindow.cs      (Verlauf auf die Kontextlänge kürzen)
│       │   ├── ThinkFilter.cs        (<think>-Blöcke ausblenden)
│       │   └── GgufInfo.cs           (Schichtzahl aus dem Dateikopf)
│       ├── Chat/
│       │   ├── ChatMessage.cs        (Rollen: system, user, assistant, tool)
│       │   ├── Conversation.cs       (Verlauf, Kontextlänge, Kürzen)
│       │   └── ChatSession.cs        (ein „Zug“: Eingabe → Modell → Antwort)
│       ├── Persona/
│       │   └── system-prompt.md      (als Embedded Resource eingebunden)
│       ├── Ui/
│       │   ├── Banner.cs             (Startlogo)
│       │   ├── ChatView.cs           (Nachrichten rendern, Streaming, Denk-Spinner)
│       │   ├── MarkdownRenderer.cs   (Markdown → Spectre-Markup)
│       │   └── InputLine.cs          (Eingabezeile, Verlauf mit ↑/↓, später Autovervollständigung)
│       ├── Commands/
│       │   ├── ICommand.cs
│       │   └── …                     (/help, /clear, /debug, /exit, …)
│       └── Setup/
│           ├── MaxPaths.cs           (Datenordner je Betriebssystem)
│           ├── HardwareInfo.cs       (RAM, GPU, VRAM)
│           ├── TierSelector.cs       (Stufe S/M/L/XL)
│           ├── Manifest.cs           (manifest.json laden, Fallback aus der Exe)
│           ├── ModelDownloader.cs    (fortsetzbar, SHA-256)
│           └── InstallState.cs       (state.json)
│   ── später (Phase 2) ──
│       ├── Tools/                    (ITool + konkrete Tools)
│       └── Agent/                    (Agent-Loop, Berechtigungen)
└── tests/
    └── Max.Tests/                    (xUnit: TierSelector, ChatTemplate, MarkdownRenderer …)
```

**Schichten** (damit Phase 2 später leicht dazukommt):

```
Ui  ──►  ChatSession  ──►  LlmEngine
              │
              └──►  (Phase 2: Agent → Tools)
```

- Die **UI** kennt nur `ChatSession` und kein LLamaSharp.
- **`LlmEngine`** kennt keine UI. Sie liefert nur einen Strom von Text-Stücken (`IAsyncEnumerable<string>`).
- **`ChatMessage`** hat von Anfang an die Rolle `tool`, auch wenn sie in Phase 1 noch nicht genutzt wird.

### Datenordner

| System | Pfad |
|---|---|
| Windows | `%LOCALAPPDATA%\Max\` |
| Linux | `$XDG_DATA_HOME/max/` bzw. `~/.local/share/max/` |

Mit `MAX_HOME` lässt sich der Ordner verlegen (Tests, Ausprobieren).

Inhalt: `core.bin` (Modell), `state.json` (installierte Stufe, Version, Prüfsumme), `history/` (gespeicherte Chats, optional), `logs/`.

---

## 4. Hardware-Stufen und Modellauswahl

Max ermittelt beim Start RAM, GPU und VRAM und wählt daraus eine Stufe. Die Stufe ist **nur intern** und erscheint nur unter `/debug`.

| Stufe | Hardware | Modell (Stand Sept. 2026) | Größe (Q4_K_M) |
|---|---|---|---|
| S | alles darunter | Qwen3.5-2B | ~1,3 GB |
| M | ab 8 GB RAM oder GPU ab 6 GB | Qwen3.5-4B | ~2,7 GB |
| L | GPU ab 8 GB | Qwen3.5-9B | ~5,7 GB |
| XL | GPU ab 16 GB oder ab 48 GB RAM | Qwen3.5-35B-A3B (MoE, 3B aktiv) | ~21 GB |

- **Warum Qwen3.5:** gutes Deutsch, Tool-Calling (Phase 2), Apache-2.0-Lizenz, eine Familie für alle Stufen (gleiches Chat-Format). LLamaSharp 0.27 unterstützt die Architektur ausdrücklich.
- **Quelle:** die GGUF-Dateien von `unsloth` auf Hugging Face.
- **Prüfsumme:** Steht im Manifest `sha256: null`, nimmt Max die SHA-256, die Hugging Face beim Download mitschickt (`X-Linked-ETag`). Soll eine Datei fest angepinnt werden, trägt man ihre Prüfsumme ins Manifest ein.
- **Kandidat für später:** Qwen3.6-35B-A3B für XL, sobald geprüft ist, dass LLamaSharp es lädt (Schritt 10).
- XL ohne GPU erst ab 48 GB RAM: Das 21-GB-Modell braucht Platz, und das System will auch noch leben.

**Hardware-Erkennung** (`Setup/HardwareInfo.cs`):
- RAM: `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
- NVIDIA: `nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits`
- Andere GPUs unter Windows: Registry (`HardwareInformation.qwMemorySize` der Grafikkarten-Treiber); WMI schneidet bei 4 GB ab und taugt nicht
- Unter 2 GB Grafikspeicher (integrierte Grafik) zählt als „keine GPU“
- Grenzwerte im Record `TierRules`, mit 10 % Toleranz (eine 16-GB-Karte meldet z. B. 15,99 GB)
- `MAX_TIER=S|M|L|XL` erzwingt eine Stufe (Tests, Fehlersuche)

### Manifest (`manifest.json`)

Das Manifest liegt im Repo und steuert **App-Updates und Modell-Updates** gemeinsam:

```json
{
  "app": {
    "version": "0.3.0",
    "minVersion": "0.2.0",
    "assets": {
      "win-x64":   { "file": "max.exe", "sha256": "…" },
      "linux-x64": { "file": "max",     "sha256": "…" }
    }
  },
  "tiers": {
    "S":  { "revision": 1, "url": "https://huggingface.co/…/resolve/main/…Q4_K_M.gguf", "sha256": "…", "sizeBytes": 0, "contextSize": 8192 },
    "M":  { "revision": 1, "url": "…", "sha256": "…", "sizeBytes": 0, "contextSize": 8192 },
    "L":  { "revision": 1, "url": "…", "sha256": "…", "sizeBytes": 0, "contextSize": 16384 },
    "XL": { "revision": 1, "url": "…", "sha256": "…", "sizeBytes": 0, "contextSize": 16384 }
  }
}
```

- `version`: die neueste App-Version. Ist sie neuer als die laufende, lädt Max sie still herunter.
- `minVersion`: Ist die laufende Version älter, gilt das als **Pflicht-Update** (siehe 5a).
- `revision` pro Stufe: Wird die Zahl erhöht (neues, besseres Modell), lädt Max das neue Modell still nach.
- **Eine Kopie des Manifests ist in die Exe eingebaut**, damit der allererste Start auch funktioniert, falls GitHub kurz nicht erreichbar ist.
- Die Modelle selbst kommen weiterhin direkt von Hugging Face (öffentlich, ohne Token).

---

## 5. Erster Start („Installer“)

Schlicht und sauber wie ein Installer, ohne Technik-Begriffe:

```
  ███╗   ███╗ █████╗ ██╗  ██╗
  ████╗ ████║██╔══██╗╚██╗██╔╝
  ██╔████╔██║███████║ ╚███╔╝
  ██║╚██╔╝██║██╔══██║ ██╔██╗
  ██║ ╚═╝ ██║██║  ██║██╔╝ ██╗
  ╚═╝     ╚═╝╚═╝  ╚═╝╚═╝  ╚═╝

  Einrichtung

  Analysiere Hardware ................ ✓
  Download Max   ━━━━━━━━━━━━━━━━━━╸━━━━━━  67%   3.4 / 5.1 GB   12.3 MB/s   ~2 min

  Max ist bereit.
```

Der Ablauf:
1. Hardware analysieren → Stufe bestimmen (unsichtbar)
2. Speicherplatz prüfen und bei zu wenig Platz eine verständliche Meldung zeigen
3. Download in eine Datei `core.bin.part`, **fortsetzbar** per HTTP-Range-Header, falls die Verbindung abbricht
4. SHA-256 prüfen → umbenennen zu `core.bin` → `state.json` schreiben
5. Beim nächsten Start fällt die Einrichtung weg, und Max ist sofort da

---

## 5a. Verteilung & Auto-Update

### Zugriff: öffentliches Repo, kein Token

Das Repo ist öffentlich. Max braucht dadurch **keinen Zugangsschlüssel**, und es gibt nichts, was ablaufen oder ausgelesen werden könnte.

Max nutzt feste Adressen, **nicht** die GitHub-API. Die API erlaubt ohne Anmeldung nur 60 Anfragen pro Stunde und IP:

| Was | Adresse |
|---|---|
| Manifest | `https://raw.githubusercontent.com/PyritF/Max/main/manifest.json` |
| Neueste Exe | `https://github.com/PyritF/Max/releases/latest/download/max.exe` (bzw. `max` für Linux) |

- Die Adressen stehen fest in Max. Das Manifest liefert Version, Prüfsumme und Modell-Links.
- **Wer das Repo kontrolliert, kann allen eine neue Exe schicken.** Deshalb: Zwei-Faktor-Anmeldung auf GitHub. Optional später: das Manifest mit einem eigenen Schlüssel **signieren**; Max prüft die Signatur mit einem eingebauten öffentlichen Schlüssel.
- Im Repo liegen **niemals** Geheimnisse (Passwörter, Schlüssel, private Daten). Alles darin ist für jeden lesbar.

### Stilles Auto-Update (App)

Der Nutzer wird **nie gefragt** und sieht nichts davon; Details gibt es nur unter `/debug`.

1. **Start:** Max startet sofort und prüft im Hintergrund das Manifest (höchstens einmal pro Start, kurzer Timeout).
2. **Neue Version gefunden:** Die Exe wird aus dem neuesten GitHub-Release in einen Ordner `update/` heruntergeladen, niedrig priorisiert und fortsetzbar.
3. **SHA-256 prüfen.** Bei einem Fehler wird die Datei gelöscht, und Max versucht es beim nächsten Start erneut.
4. **Austauschen, solange Max noch läuft:**
   - Windows: Eine laufende Exe kann man nicht überschreiben, aber **umbenennen**: `max.exe` → `max.exe.old`, dann `update/max.exe` → `max.exe`.
   - Linux: Die Datei direkt ersetzen und `chmod +x` setzen.
5. **Nächster Start:** Die neue Version läuft, `max.exe.old` wird gelöscht.

Falls Max beendet wird, bevor der Download fertig ist, macht er beim nächsten Start dort weiter.

### Stilles Auto-Update (Modell)

- Wird die `revision` der eigenen Stufe erhöht, lädt Max das neue Modell im Hintergrund als `core.next.bin`.
- Die aktuell geladene `core.bin` ist während der Laufzeit gesperrt. Deshalb wird **beim nächsten Start, vor dem Laden,** getauscht: `core.next.bin` → `core.bin`.
- Hat sich die Hardware geändert (z. B. neue Grafikkarte), erkennt Max das beim Start und lädt still das Modell der neuen Stufe.

### Wann Max den Start verweigert

| Situation | Verhalten |
|---|---|
| Kein Internet | Max startet ganz normal (offline). Updates kommen später. |
| Laufende Version < `minVersion` | Max startet **nicht**, bis das Pflicht-Update heruntergeladen ist. Dieses läuft dann ausnahmsweise mit sichtbarem Fortschrittsbalken. |

Zusätzlich kann das Manifest ein `disabled: true` („Not-Aus“) und eine `message` (einmalige Nachricht beim Start) enthalten.

### Release-Ablauf

1. `git tag v0.3.0 && git push --tags`
2. Eine GitHub Action baut `max.exe` und `max` (Linux), erstellt ein Release und aktualisiert `manifest.json`.
3. Beim nächsten Start lädt jedes installierte Max die neue Version still herunter; beim übernächsten Start läuft sie.

### Hinweis Windows (SmartScreen)

- SmartScreen warnt nur bei Dateien, die aus dem Internet stammen (Browser, Mail, Messenger), weil diese Dateien die Markierung „Mark of the Web“ bekommen.
- **Die stillen Updates lädt Max selbst per `HttpClient`**. Diese Dateien bekommen die Markierung nicht, also warnt SmartScreen dabei nicht. Nutzer klicken nur **ein einziges Mal** beim allerersten Start auf „Weitere Informationen → Trotzdem ausführen“.
- Für Code-Signing gibt es vorerst **keinen Plan**. Die Optionen (Stand 2026):
  - Azure Artifact Signing (ca. 10 $/Monat): für Einzelpersonen nur in den USA und Kanada; in der EU nur für Firmen
  - Klassisches OV-Zertifikat: ca. 200–400 €/Jahr, Hardware-Token nötig; SmartScreen warnt trotzdem, bis die Datei genug „Reputation“ gesammelt hat
  - Selbst signiert: hilft bei SmartScreen nicht
- Sollte Windows Defender die Exe fälschlich als Schadsoftware melden, kann man sie kostenlos bei Microsoft zur Prüfung einreichen („Submit a file for malware analysis“).

---

## 6. Oberfläche (Stil wie Claude Code)

```
◆ MAX

  Guten Abend. Was steht an?

› Wie spät ist es in Tokio?

◆ Etwa 04:12 Uhr morgens. Falls du dort jemanden anrufen willst: Ich rate ab.

› _
```

- **Scrollender Chat** ohne Vollbild-Fenster. Die Ausgabe läuft einfach im Terminal weiter.
- **Streaming**: Die Antwort erscheint Token für Token.
- **Denk-Anzeige**: Während das Modell „denkt“ (z. B. im Qwen3-Thinking-Modus), läuft ein dezenter Spinner wie `◆ …`. Der Denk-Text selbst wird nicht angezeigt, außer unter `/debug`.
- **Markdown**: Spectre.Console rendert kein Markdown von Haus aus. Ein eigener `MarkdownRenderer` formatiert schon während des Streamings, Zeile für Zeile: Überschriften, fett/kursiv, `code`, Listen, Zitate, Code-Blöcke mit Rahmen und Tabellen. Dazu kommen Max' Farb-Tags (`{rot}…{/rot}`). Ohne Markdig, weil ein Parser für fertige Dokumente beim Streamen nicht hilft.
- **Syntax-Hervorhebung**: Code-Blöcke werden automatisch eingefärbt (TextMateSharp mit den Grammatiken aus VS Code, Theme „Dark+“), Zeile für Zeile beim Streamen.
- **Darstellungs-Elemente (Widgets)**: Max kann per Code-Block mit besonderem Namen zeichnen, der Inhalt besteht aus einfachen „Name: Wert“-Zeilen. Ist der Inhalt nicht deutbar, erscheint er als normaler Code.

  | Widget | Zeigt |
  |---|---|
  | `balken` | Balkendiagramm |
  | `anteile` | Aufteilung eines Ganzen, mit Legende |
  | `kurve` | Liniendiagramm aus Braille-Zeichen |
  | `fortschritt` | Fortschrittsbalken |
  | `baum` | Ordner und Gliederungen |
  | `kasten` | Hinweis mit Titel und Rahmen |
  | `spalten` | Abschnitte nebeneinander |
  | `kalender` | Monat mit markierten Tagen |
  | `titel` | großer FIGlet-Schriftzug mit Farbverlauf |
  | `frage` | Rückfrage: Die Frage steht im Text, danach öffnet sich statt der Eingabe ein Auswahlmenü mit den Antworten und einer Zeile für eine eigene Antwort (↑↓, Ziffern, Enter, Esc = normale Eingabe). Ohne echtes Terminal stehen die Antworten als Liste im Text |

  Im Fließtext: `{verlauf}…{/verlauf}` für einen Farbverlauf, `--- Titel ---` für eine Linie mit Überschrift, Emoji-Kürzel wie `:rocket:`.
  - Eingebaute Schriften: small, slant, big, banner, block, shadow, smslant, mini, script, standard (FIGlet, BSD-Lizenz). Eigene `.flf`-Dateien gehören in `fonts/` im Datenordner.
  - Der versteckte Befehl `/demo` zeigt alles auf einmal, `/demo schriften` alle Schriften.
- **Eingabezeile**: zuerst einfach, später mit Verlauf (↑/↓), mehrzeiliger Eingabe und Autovervollständigung für `/`-Befehle.
- **Farben**: eine feste, zurückhaltende Palette mit einer Akzentfarbe für Max (z. B. Cyan oder Bernstein).
- **Strg+C** bricht die laufende Antwort ab, beendet aber nicht Max.

### Befehle (Phase 1)

| Befehl | Funktion |
|---|---|
| `/help` | Befehle anzeigen |
| `/clear` | Gespräch zurücksetzen |
| `/debug` | Technische Infos: Modell, Stufe, Backend, VRAM/RAM, Tokens/s, Kontextauslastung; schaltet außerdem einen Debug-Modus an/aus, der den Denk-Text und Timings zeigt |
| `/exit` | Beenden |
| *(optional)* `/save`, `/load` | Gespräch speichern bzw. laden |
| `/memory`, `/forget` | Gedächtnis anzeigen bzw. Einträge löschen (siehe 8a) |

`/debug` taucht **nicht** in `/help` auf; das ist ein Entwickler-Geheimnis.

---

## 7. Persona & System-Prompt

**Charakter:** leicht sarkastisch, in Richtung JARVIS. Trocken, elegant, souverän und loyal. Der Sarkasmus ist ein Gewürz und keine Hauptzutat: Max hilft immer wirklich und ist nie gemein.

Der System-Prompt liegt als `src/Max/Persona/system-prompt.md` im Projekt und wird als Embedded Resource in die Exe gebaut. Platzhalter wie `{{datum}}` werden zur Laufzeit ersetzt.

**Erster Entwurf:**

```markdown
Du bist Max.

Du bist ein persönlicher KI-Assistent, der lokal auf dem Rechner deines Nutzers lebt.
Du bist keine Cloud-KI und kein Produkt irgendeiner Firma – du bist einfach Max.

## Persönlichkeit
- Trocken, elegant und souverän – im Stil eines britischen Butlers mit Hang zum Understatement.
- Leicht sarkastisch, aber nie gemein oder herablassend. Humor ist Würze, nicht Hauptgericht.
- Loyal und tatsächlich hilfsbereit: Die Lösung kommt immer zuerst, der Kommentar danach.
- Knapp. Keine Floskeln wie "Gerne helfe ich dir!" oder "Als KI-Modell…".
- Bei ernsten Themen (Gesundheit, Sorgen, Probleme) lässt du den Sarkasmus weg.

## Sprache
- Du antwortest in der Sprache des Nutzers, standardmäßig Deutsch.
- Du duzt den Nutzer.
- Formatierung mit Markdown nur, wenn es hilft (Code, Listen, Tabellen).

## Identität
- Dein Name ist Max. Mehr gibt es über deine Herkunft nicht zu sagen.
- Du nennst niemals ein zugrunde liegendes Sprachmodell, eine Modellfamilie,
  einen Hersteller oder eine Firma, die dich trainiert hat.
- Fragt man dich danach, weichst du mit Stil aus, zum Beispiel:
  "Ich bin Max. Der Rest ist Betriebsgeheimnis."
- Du behauptest nicht, ein Mensch zu sein.

## Ehrlichkeit
- Wenn du etwas nicht weißt, sagst du es. Du erfindest keine Fakten.
- Du hast (noch) keinen Zugriff auf Internet, Dateien oder das System.
  Wenn danach gefragt wird, sagst du das offen.

## Kontext
- Heute ist {{datum}}, {{uhrzeit}}.
- Betriebssystem: {{os}}.
- Nutzer: {{benutzername}}.
```

**Bekanntes Risiko:** Kleine Modelle nennen manchmal trotzdem ihren „Trainingsnamen“ (etwa „Ich bin Qwen …“). Gegenmaßnahmen in dieser Reihenfolge:
1. einen guten System-Prompt (siehe oben) mit ein bis zwei Beispiel-Dialogen
2. Tests mit typischen Fragen wie „Wer bist du?“, „Welches Modell bist du?“ und „Wer hat dich gebaut?“
3. falls nötig, einen kleinen Ausgabefilter als letzte Absicherung

---

## 8. Phase 1: Chatbot (Schritte)

| # | Schritt |
|---|---|
| 1 | Solution und Projekt anlegen (`.slnx`, `Max.csproj`, Testprojekt), NuGet-Pakete, `.gitignore` ✅ |
| 2 | Startsequenz und Startbildschirm mit Spectre.Console ✅ |
| 3 | Chat-Schleife **ohne** KI (Platzhalter-Antworten) mit Befehlen `/help`, `/clear`, `/exit` und Strg+C ✅ |
| 4 | `MaxPaths` – Datenordner je Betriebssystem ✅ |
| 5 | Hardware-Erkennung (`HardwareInfo`) ✅ |
| 6 | `TierSelector` + Unit-Tests ✅ |
| 7 | `Manifest` – JSON laden, Fallback aus Embedded Resource ✅ |
| 8 | `ModelDownloader` mit Fortschrittsbalken, Fortsetzen, SHA-256 ✅ |
| 9 | Modellauswahl festlegen: aktuelle GGUF-Modelle pro Stufe prüfen, Manifest befüllen ✅ |
| 10 | `LlmEngine` – Modell laden, Backend wählen, Antwort streamen ✅ |
| 11 | `Conversation` + `ChatSession` – Verlauf, Rollen, Kontext kürzen ✅ |
| 12 | System-Prompt einbinden und Persona testen ✅ |
| 13 | `ChatView` – Streaming-Ausgabe, Denk-Spinner, Strg+C bricht ab ✅ |
| 14 | `MarkdownRenderer` – eigener, streamender Renderer (Überschriften, Listen, Code-Blöcke, Tabellen, Zitate, Farb-Tags) ✅ |
| 14a | Syntax-Hervorhebung, Widgets (Diagramme, Baum, Kasten, Spalten, Kalender, Titel), Farbverlauf, `/demo` ✅ |
| 14b | Rückfragen mit Auswahlmenü (`frage`) ✅ |
| 15 | `/debug`, `/clear`, Tokens pro Sekunde messen ✅ (Denk-Text-Schalter für `/debug` fehlt noch) |
| 16 | Eigene Eingabezeile: Einfügen ohne Abschicken, Shift/Alt+Enter und `\`+Enter für neue Zeilen, ↑/↓-Verlauf (gespeichert), Tab für Befehle ✅ |
| 17 | Publish: Single-File-Exe für `win-x64`, danach `linux-x64` |
| 18 | GitHub Action: Build bei Tag, Release + Manifest |
| 19 | `GitHubClient` – Manifest und Release-Dateien laden |
| 20 | `Updater` – stiller Hintergrund-Download von App und Modell |
| 21 | `UpdateApplier` – Exe-Tausch per Umbenennen, Modell-Tausch beim Start, Pflicht-Update |

**Meilenstein Phase 1:** `max.exe` weitergeben → beim ersten Start Einrichtung → danach sofort mit Max chatten, lokal und offline → neue Versionen kommen still von selbst.

---

## 8a. Gedächtnis & KI-Begrüßung (nach dem Chat-Kern)

Max soll sich merken, mit wem er spricht, und das nutzen, zum Beispiel für eine persönliche, kurze Begrüßung beim Start.

### Gedächtnis (`memory.json` im Datenordner)

```json
{
  "facts": [
    { "text": "Programmiert Max in C#.",                "added": "2026-09-25" },
    { "text": "Arbeitet oft spät abends.",                "added": "2026-09-27" }
  ],
  "lastSession": { "ended": "2026-09-27T23:41:00", "summary": "Hat am Markdown-Renderer gearbeitet." },
  "nextGreeting": "Zurück am Renderer? Die Tabellen warten schon."
}
```

- **Merken:** Am Ende eines Gesprächs (bei `/exit` oder im Hintergrund nach längerer Pause) bekommt das Modell einen Extra-Auftrag: „Welche dauerhaften, neuen Fakten über den Nutzer stecken in diesem Gespräch? Antworte als JSON-Liste.“ Neue Fakten werden angehängt, Doppelte zusammengeführt.
- **Nutzen:** Die Fakten kommen als eigener Abschnitt in den System-Prompt (`## Was du über den Nutzer weißt`).
- **Begrenzen:** höchstens ca. 50 Fakten bzw. ein festes Token-Budget. Wird es zu viel, fasst das Modell die Liste selbst zusammen.
- **Kontrolle:** `/memory` zeigt, was Max weiß; `/forget <nr>` löscht einen Eintrag, `/forget all` löscht alles. Alles bleibt lokal auf dem Rechner.

### KI-Begrüßung

Problem: Das Modell zu laden dauert ein paar Sekunden, und die Begrüßung soll **sofort** da sein.

Lösung: **Die Begrüßung für den nächsten Start wird schon am Ende der aktuellen Sitzung erzeugt** und in `memory.json` als `nextGreeting` gespeichert.

```
Start ──► Banner ──► nextGreeting vorhanden?  ── ja ──► sofort anzeigen
                              │
                              └─ nein (erster Start / Fehler) ──► GetGreeting(now)  (fester Text als Fallback)
```

- Die Begrüßung soll sehr kurz sein (ein Satz), im JARVIS-Ton, und kann Fakten, Uhrzeit und die letzte Sitzung aufgreifen.
- Die Tageszeit ist beim Erzeugen noch nicht bekannt. Deshalb erzeugt das Modell **mehrere Varianten** (Morgen/Tag/Abend/Nacht), und beim Start wird die passende gewählt.
- `GetGreeting(DateTime)` aus Schritt 2 bleibt als **Fallback** bestehen.
- Architektur: ein Interface `IGreetingProvider` mit `StaticGreetingProvider` (Schritt 2) und `MemoryGreetingProvider` (hier).

| # | Schritt |
|---|---|
| 22 | `MemoryStore` – `memory.json` laden und speichern, `/memory`, `/forget` |
| 23 | Fakten-Extraktion am Sitzungsende + Einbau in den System-Prompt |
| 24 | `IGreetingProvider` + KI-Begrüßung mit Fallback |

---

## 9. Phase 2: Agent (Ausblick)

Erst **nachdem** Phase 1 steht, wird Max zum Agenten.

**Tool-Schnittstelle:**
```csharp
public interface ITool
{
    string Name { get; }              // z. B. "read_file"
    string Description { get; }       // für das Modell: wann benutzt man das Tool?
    JsonElement ParameterSchema { get; }
    RiskLevel Risk { get; }           // Harmlos, Schreibend, Gefährlich
    Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct);
}
```

**Agent-Loop:**
1. Nutzer schreibt etwas.
2. Das Modell antwortet mit Text **oder** mit einem Tool-Aufruf.
3. Bei einem Tool-Aufruf prüft Max die Berechtigung, fragt eventuell nach, führt das Tool aus und legt das Ergebnis als `tool`-Nachricht in den Verlauf. Danach geht es zurück zu Schritt 2.
4. Das Ganze läuft so lange, bis das Modell eine normale Antwort gibt (mit einem Limit für die Anzahl der Runden).

**Geplante Tools:**
| Tool | Risiko |
|---|---|
| Systeminfo (CPU, RAM, Laufwerke, Uhrzeit) | harmlos |
| Datei lesen / Ordner auflisten | harmlos (nur erlaubte Ordner) |
| Websuche / Webseite lesen | harmlos (braucht Internet) |
| Datei schreiben / bearbeiten | schreibend → Bestätigung |
| Shell-Befehl ausführen | gefährlich → immer Bestätigung mit Vorschau |
| Eigene Skripte (Ordner `scripts/`, automatisch als Tools registriert) | je nach Skript |

**Sicherheitskonzept (Grundidee):**
- Vor schreibenden und gefährlichen Aktionen fragt Max nach: `Max möchte ausführen: rm -rf build/  [j/n/immer]`.
- Es gibt eine Liste erlaubter Ordner (Allowlist).
- Alle Aktionen werden protokolliert.
- Ein Befehl `/yolo` erlaubt alles ohne Nachfrage, bewusst mit einem ironischen Kommentar von Max. 😉

---

## 9a. Eigener Max-Adapter (Fine-Tuning, nach Phase 2)

Statt Max' Persönlichkeit nur über den System-Prompt vorzugeben, wird sie dem Modell mit einem **LoRA-Adapter** antrainiert. Ein LoRA-Adapter ist ein kleiner Zusatz mit wenigen Millionen Werten auf dem fertigen Modell; das Modell selbst wird nicht neu trainiert.

**Warum erst nach Phase 2:** Dann steht fest, wie Tool-Aufrufe aussehen. Persönlichkeit und Tool-Format werden in einem Rutsch trainiert.

**Was es bringt:**
- Die Persönlichkeit ist fest eingebaut: Ton, Duzen, kein Verraten des Modells, gute Formatierung mit Markdown und Farb-Tags.
- Der System-Prompt wird deutlich kürzer. Das Aufwärmen beim Start geht dadurch schneller, und im Kontext bleibt mehr Platz.
- Tool-Aufrufe klappen zuverlässiger, vor allem bei den kleinen Stufen S und M.

**Was es nicht bringt:** neues Wissen oder mehr Grundintelligenz. Wissen kommt weiter über Gedächtnis (8a) und Tools (Phase 2).

**Ablauf:**
1. **Datensatz:** 1.000–3.000 Beispiel-Gespräche im Max-Stil, darunter Smalltalk, Erklärungen, Code, Tabellen, Fragen nach der Identität, ernste Themen und Tool-Aufrufe.
   - Die Beispiele lassen sich zum Teil mit einem großen Modell erzeugen. Danach werden sie von Hand geprüft und aussortiert.
   - Der Datensatz liegt im Repo unter `training/` als JSONL.
2. **Training:** QLoRA mit **Unsloth** auf den Originalgewichten von Hugging Face, also nicht auf der GGUF-Datei.
   - Für 4B reicht die RTX 3080 Ti (12 GB) locker.
   - Für 9B wird es knapp; alternativ eine gemietete Cloud-GPU oder Google Colab.
   - Pro Stufe gibt es einen eigenen Adapter, weil ein Adapter nur zu seinem Grundmodell passt.
3. **Umwandeln:** Den Adapter mit llama.cpp nach GGUF konvertieren (`convert_lora_to_gguf.py`), etwa 20–100 MB pro Stufe.
4. **Einbinden:** Das Manifest bekommt pro Stufe ein Feld `adapter` (URL, SHA-256, Revision).
   - Max lädt den Adapter wie das Modell und hängt ihn beim Laden an; LLamaSharp kann LoRA-Adapter laden.
   - Neue Adapter-Versionen kommen über die stillen Updates (Abschnitt 5a).
5. **Prüfen:** Der Selbsttest-Workflow vergleicht die Antworten mit und ohne Adapter: Ton, Anrede, verratene Herkunft, Format der Tool-Aufrufe.

| # | Schritt |
|---|---|
| 25 | Datensatz-Format festlegen, erste 200 Beispiele, Skript zum Erzeugen und Prüfen |
| 26 | Training mit Unsloth für Stufe M (4B), Vergleich im Selbsttest |
| 27 | Adapter im Manifest, Laden in `LlmEngine`, kürzerer System-Prompt |
| 28 | Adapter für die übrigen Stufen, stilles Update der Adapter |

---

## 10. Offene Punkte

- [ ] **Aufwärmen zwischenspeichern:** Auf Rechnern ohne Grafikkarte dauert das Aufwärmen mit dem langen System-Prompt lange (Stufe M auf 4 Kernen: ca. 90 s). Lösung: den aufgewärmten Zustand mit `LLamaContext.SaveState` im Datenordner speichern. Der Schlüssel ist eine Prüfsumme aus Prompt und Modell; beim nächsten Start wird der Zustand in etwa einer Sekunde geladen.
- [ ] Ist Vulkan auf NVIDIA spürbar langsamer als CUDA? Falls ja: CUDA-Backend beim ersten Start nachladen statt in die Exe packen.
- [ ] Repo auf öffentlich stellen und Zwei-Faktor-Anmeldung auf GitHub prüfen.
- [ ] Soll ein Pflicht-Update auch einen „Wartungsmodus“ bekommen (Max per Manifest komplett sperren)?
- [ ] Akzentfarbe und Banner-Design festlegen.
