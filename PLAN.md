# Max – Projektplan

> Max ist ein lokaler KI-Assistent fürs Terminal. Er läuft komplett auf dem eigenen Rechner, ohne Cloud und ohne API-Key, und kommt als eine einzige Datei für Windows (`max.exe`) und Linux (`max`).
> Phase 1 ist ein Chatbot. In Phase 2 wird Max zum Agenten, der Tools ausführen kann.

---

## 1. Entscheidungen

| Thema | Entscheidung |
|---|---|
| Sprache / Plattform | **C# mit .NET 10** |
| LLM-Engine | **LLamaSharp** (C#-Bindings für llama.cpp) mit Backends für CPU, CUDA und Vulkan |
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
│       │   ├── LlmEngine.cs          (Modell laden, Backend wählen, Tokens streamen)
│       │   └── ChatTemplate.cs       (Verlauf → Prompt im Format des Modells)
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
│       └── Config/
│           └── MaxPaths.cs           (Datenordner je Betriebssystem)
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
| Linux | `~/.local/share/max/` |

Inhalt: `core.bin` (Modell), `state.json` (installierte Stufe, Version, Prüfsumme), `history/` (gespeicherte Chats, optional), `logs/`.

---

## 4. Hardware-Stufen und Modellauswahl

Max ermittelt beim Start RAM, GPU und VRAM und wählt daraus eine Stufe. Die Stufe ist **nur intern** und erscheint nur unter `/debug`.

| Stufe | Hardware (grob) | Modell (Stand der Planung) | Größe |
|---|---|---|---|
| S | nur CPU, < 8 GB RAM | Qwen3-1.7B | ~1,1 GB |
| M | 8–16 GB RAM oder GPU mit ~6 GB | Qwen3-4B | ~2,5 GB |
| L | GPU mit 8–12 GB | Qwen3-8B | ~5 GB |
| XL | GPU ab 16 GB oder ≥ 32 GB RAM | Qwen3-30B-A3B oder gpt-oss-20b | ~12–18 GB |

> ⚠️ Die Modellliste wird **kurz vor der Umsetzung neu geprüft**, weil ständig neue und bessere Modelle erscheinen. Wichtigstes Kriterium ist zuverlässiges Tool-Calling (für Phase 2) und gutes Deutsch.

**Hardware-Erkennung:**
- RAM: `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, alternativ WMI unter Windows oder `/proc/meminfo` unter Linux
- NVIDIA-GPU und VRAM: `nvidia-smi --query-gpu=name,memory.total --format=csv` bzw. NVML
- Andere GPUs: Vulkan-Abfrage bzw. DXGI unter Windows; sonst läuft Max vorsichtig auf der CPU
- Die Grenzwerte liegen in einer Konfiguration und sind nicht hart im Code verdrahtet

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
- **Markdown**: Spectre.Console rendert kein Markdown von Haus aus. Wir bauen einen eigenen kleinen `MarkdownRenderer` (mit **Markdig** zum Parsen) für Überschriften, fett, Listen, Codeblöcke und Tabellen. Während des Streamings kommt die Ausgabe als Rohtext, danach wird sie sauber formatiert.
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
| 4 | `MaxPaths` – Datenordner je Betriebssystem |
| 5 | Hardware-Erkennung (`HardwareInfo`) |
| 6 | `TierSelector` + Unit-Tests |
| 7 | `Manifest` – JSON laden, Fallback aus Embedded Resource |
| 8 | `ModelDownloader` mit Fortschrittsbalken, Fortsetzen, SHA-256 |
| 9 | Modellauswahl festlegen: aktuelle GGUF-Modelle pro Stufe prüfen, Manifest befüllen |
| 10 | `LlmEngine` – Modell laden, Backend wählen, Antwort streamen |
| 11 | `Conversation` + `ChatSession` – Verlauf, Rollen, Kontext kürzen |
| 12 | System-Prompt einbinden und Persona testen |
| 13 | `ChatView` – Streaming-Ausgabe, Denk-Spinner, Strg+C bricht ab |
| 14 | `MarkdownRenderer` mit Markdig |
| 15 | `/debug`, `/clear`, Tokens pro Sekunde messen |
| 16 | Bessere Eingabezeile (Verlauf, Mehrzeilen, Autovervollständigung) |
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

## 10. Offene Punkte

- [ ] Modellliste aktualisieren: Welche GGUF-Modelle sind zum Start der Umsetzung die besten pro Stufe?
- [ ] Wie groß wird die Exe mit CUDA-Backend (kann mehrere 100 MB sein)? Eventuell auch das GPU-Backend beim ersten Start nachladen.
- [ ] Repo auf öffentlich stellen und Zwei-Faktor-Anmeldung auf GitHub prüfen.
- [ ] Soll ein Pflicht-Update auch einen „Wartungsmodus“ bekommen (Max per Manifest komplett sperren)?
- [ ] Akzentfarbe und Banner-Design festlegen.
