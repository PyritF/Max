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
| Modell | **Ein einziges Modell: Qwen3.5-9B** (GGUF, Q4_K_M, ~5,7 GB). Früher gab es vier Stufen (2B bis 35B); die kleinen waren zu unzuverlässig, das große passt auf keinen üblichen Rechner |
| Voraussetzung | **Grafikkarte ab 4 GB** und 8 GB Arbeitsspeicher (16 GB bei Karten unter 6 GB) (siehe Abschnitt 4) |
| Modellquelle | **Hugging Face**: direkte Download-Links, kein eigenes Hosting nötig |
| Zielsysteme | Windows x64 zuerst, danach Linux x64 |
| Auslieferung | Single-File-Publish; das Modell wird beim ersten Start heruntergeladen |
| Verteilung | Das Repo `PyritF/Max` ist **öffentlich**; Manifest und Releases werden ohne Token direkt von GitHub geladen (siehe Abschnitt 5a) |
| Updates | **Still im Hintergrund**: Download beim Start, beim nächsten Start ist die neue Version aktiv, ohne Rückfrage |
| Nutzung | privates Projekt, Lizenzfragen daher zweitrangig |

---

## 2. Grundprinzip: Max ist Max

Max soll wie ein eigenständiges Wesen wirken und nicht wie „noch eine KI mit Modell X dahinter“.

- **Kein Modellname in der normalen Oberfläche.** Der Nutzer sieht nur „Max“.
- **Max entscheidet selbst**, welches Modell zur Hardware passt. Der Nutzer wählt nichts aus.
- **Technische Details gibt es nur unter `/debug`**: Modell, Backend (CPU/Vulkan), VRAM, Tokens pro Sekunde, Kontextgröße.
- **Neutrale Dateinamen auf der Festplatte.** Modelldateien heißen zum Beispiel `core.bin` und nicht `Qwen3-8B-Q4_K_M.gguf`.
- **Eigener System-Prompt** mit fester Identität (siehe Abschnitt 7). Max verrät nie, welches Modell oder welche Firma dahintersteckt.

---

## 3. Architektur

```
Max/
├── PLAN.md
├── manifest.json               ← aktuelle App-Version + Modell, wird von Max online gelesen
├── src/
│   └── Max/
│       ├── Max.csproj
│       ├── Program.cs          ← Einstiegspunkt: Setup → Chat-Loop
│       ├── Setup/              ← Erststart / „Installer“
│       │   ├── HardwareInfo.cs       (RAM, GPU, VRAM, CPU-Kerne)
│       │   ├── Requirements.cs       (reicht der Rechner? Grafikkarte, RAM)
│       │   ├── Manifest.cs           (manifest.json laden, eingebauter Fallback)
│       │   └── ModelDownloader.cs    (Download mit Fortschritt, Fortsetzen, Prüfsumme)
│       ├── Update/
│       │   ├── AppVersion.cs         (laufende Version, Vergleich, Pfad der Exe)
│       │   ├── StartupUpdate.cs      (Schritt „Suche nach Updates“: Not-Aus, Pflicht-Update)
│       │   ├── Updater.cs            (Hintergrund-Download von App und Modell, Exe-Tausch)
│       │   └── UpdateApplier.cs      (bereitgelegtes Modell beim Start aktivieren, .old aufräumen)
│       ├── Llm/
│       │   ├── LlmEngine.cs          (Modell laden, Backend wählen, Tokens streamen, Cache wiederverwenden)
│       │   ├── LlmBackend.cs         (System-Prompt + Verlauf → Prompt → Antwort)
│       │   ├── ChatTemplate.cs       (Verlauf → Prompt im Format des Modells, ChatML)
│       │   ├── ContextWindow.cs      (Verlauf auf die Kontextlänge kürzen)
│       │   ├── ThinkSplitter.cs      (Nachdenken und Antwort trennen)
│       │   ├── AnswerGrammar.cs      (GBNF: nur gültige Tags und Elemente)
│       │   ├── GrammarSampler.cs     (Token ziehen, mit Grammatik-Prüfung)
│       │   ├── ElementGate.cs        (Elemente zurückhalten, prüfen, ggf. neu erzeugen)
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
│           ├── Requirements.cs       (Grafikkarte ab 4 GB, RAM ab 8 bzw. 16 GB)
│           ├── Manifest.cs           (manifest.json laden, Fallback aus der Exe)
│           ├── ModelDownloader.cs    (fortsetzbar, SHA-256)
│           └── InstallState.cs       (state.json)
│   ── später (Phase 2) ──
│       ├── Tools/                    (ITool + konkrete Tools)
│       └── Agent/                    (Agent-Loop, Berechtigungen)
└── tests/
    └── Max.Tests/                    (xUnit: Requirements, ChatTemplate, MarkdownRenderer …)
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

Inhalt: `core.bin` (Modell), `state.json` (installiertes Modell, Version, Prüfsumme), `cache/` (gerechneter System-Prompt), `history/` (gespeicherte Chats, optional), `logs/`.

---

## 4. Modell und Voraussetzungen

Max nutzt **ein einziges Modell: Qwen3.5-9B** (Q4_K_M, ~5,7 GB, Kontext 16.384 Tokens, Denk-Budget 1.024 Tokens).

**Warum nur eines (Entscheidung vom September 2026):** Anfangs gab es vier Stufen je nach Rechner – 2B, 4B, 9B und 35B-A3B. Die Selbsttests zeigten: 2B und 4B erfinden Fakten, bauen sinnlose Diagramme und ignorieren Stilwünsche; mit Regeln war das nicht zu beheben. Das 35B-Modell braucht über 20 GB Grafik- bzw. Arbeitsspeicher. Ein Modell heißt außerdem: ein Prompt, ein Satz Tests, kein Wechseln zwischen Modellen.

- **Warum Qwen3.5:** gutes Deutsch, Tool-Calling (Phase 2), Apache-2.0-Lizenz. LLamaSharp 0.27 unterstützt die Architektur ausdrücklich.
- **Quelle:** die GGUF-Datei von `unsloth` auf Hugging Face.
- **Prüfsumme:** Steht im Manifest `sha256: null`, nimmt Max die SHA-256, die Hugging Face beim Download mitschickt (`X-Linked-ETag`). Soll eine Datei fest angepinnt werden, trägt man ihre Prüfsumme ins Manifest ein.
- **Später denkbar:** das 35B-A3B-Modell als Option für sehr starke Rechner.

**Voraussetzungen** (`Setup/Requirements.cs`), geprüft beim Schritt „Analysiere Hardware“ – **vor** dem Download:

| | Mindestens | Warum |
|---|---|---|
| Grafikkarte | 4 GB Speicher (ab 8 GB passt das Modell ganz darauf; unter 6 GB rechnet der Prozessor einen guten Teil mit und es braucht 16 GB Arbeitsspeicher) | Ohne Grafikkarte dauert eine Antwort eine halbe bis ganze Minute |
| Arbeitsspeicher | 8 GB | Sonst passt das Modell nicht |

- Fehlt etwas, sagt Max das in einem Satz („Ich brauche eine Grafikkarte mit mindestens 4 GB Speicher – auf diesem Rechner habe ich keine gefunden.“) und lädt nichts herunter.
- Grenzwerte in GB wie auf dem Karton, mit 10 % Toleranz (eine 6-GB-Karte meldet etwas weniger).
- `MAX_CPU=1` erlaubt den Start ohne Grafikkarte – für den Selbsttest auf GitHub (keine Grafikkarte) und falls die Erkennung eine Karte übersieht.
- Scheitert das Laden auf der Grafikkarte trotzdem, versucht Max es auf der CPU; `/debug` zeigt „CPU (Grafikkarte fehlgeschlagen)“.

**Hardware-Erkennung** (`Setup/HardwareInfo.cs`):
- RAM: `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`
- NVIDIA: `nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits`
- Andere GPUs unter Windows: Registry (`HardwareInformation.qwMemorySize` der Grafikkarten-Treiber); WMI schneidet bei 4 GB ab und taugt nicht
- Unter 2 GB Grafikspeicher (integrierte Grafik) zählt als „keine GPU“
- **Lücke:** AMD- und Intel-Karten unter Linux werden noch nicht erkannt (später z. B. über `vulkaninfo`); bis dahin hilft `MAX_CPU=1`, geladen wird trotzdem über Vulkan

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
  "model": { "revision": 1, "url": "https://huggingface.co/…/resolve/main/…Q4_K_M.gguf", "sha256": "…", "sizeBytes": 0, "contextSize": 16384, "thinkingBudget": 1024 }
}
```

- `version`: die neueste App-Version. Ist sie neuer als die laufende, lädt Max sie still herunter.
- `minVersion`: Ist die laufende Version älter, gilt das als **Pflicht-Update** (siehe 5a).
- `revision`: Wird die Zahl erhöht (neues, besseres Modell), lädt Max das neue Modell still nach.
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
1. Hardware analysieren → reicht der Rechner nicht, freundliche Meldung und Schluss (siehe Abschnitt 4)
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
| Exe einer Version | `https://github.com/PyritF/Max/releases/download/v0.2.0/max.exe` (bzw. `max` für Linux) – passend zur Prüfsumme im Manifest |

- Die Adressen stehen fest in Max. Das Manifest liefert Version, Prüfsumme und Modell-Links.
- **Wer das Repo kontrolliert, kann allen eine neue Exe schicken.** Deshalb: Zwei-Faktor-Anmeldung auf GitHub. Optional später: das Manifest mit einem eigenen Schlüssel **signieren**; Max prüft die Signatur mit einem eingebauten öffentlichen Schlüssel.
- Im Repo liegen **niemals** Geheimnisse (Passwörter, Schlüssel, private Daten). Alles darin ist für jeden lesbar.

### Stilles Auto-Update (App)

Der Nutzer wird **nie gefragt** und sieht nichts davon; Details gibt es nur unter `/debug`.

1. **Start:** Max startet sofort und prüft im Hintergrund das Manifest (höchstens einmal pro Start, kurzer Timeout).
2. **Neue Version gefunden:** Die Exe wird aus dem GitHub-Release dieser Version in den Ordner `update/` heruntergeladen, fortsetzbar. Beim Schließen pausiert der Download und geht beim nächsten Start weiter.
3. **SHA-256 prüfen.** Bei einem Fehler wird die Datei gelöscht, und Max versucht es beim nächsten Start erneut.
4. **Austauschen, solange Max noch läuft:**
   - Windows: Eine laufende Exe kann man nicht überschreiben, aber **umbenennen**: `max.exe` → `max.exe.old`, dann `update/max.exe` → `max.exe`.
   - Linux: Die Datei direkt ersetzen und `chmod +x` setzen.
5. **Nächster Start:** Die neue Version läuft, `max.exe.old` wird gelöscht.

Falls Max beendet wird, bevor der Download fertig ist, macht er beim nächsten Start dort weiter.

### Stilles Auto-Update (Modell)

- Wird die `revision` des Modells erhöht, lädt Max das neue Modell im Hintergrund als `core.next.bin`.
- Die aktuell geladene `core.bin` ist während der Laufzeit gesperrt. Deshalb wird **beim nächsten Start, vor dem Laden,** getauscht: `core.next.bin` → `core.bin`.

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
- **Nachdenken:** Vor jeder Antwort denkt Max nach (Denkmodus des Modells). Der Denk-Text läuft grau und kursiv mit (letzte 6 Zeilen) und verschwindet, sobald die Antwort beginnt. Budget im Manifest (`thinkingBudget`: 1024 Tokens), danach wird das Nachdenken beendet. `/denken an|aus` schaltet es, gemerkt in `settings.json`. Das Nachdenken kommt nicht in den Verlauf: Vor der Antwort merkt sich die Engine einen Zwischenstand (`LLamaContext.GetState`), springt danach zurück und rechnet nur die Antwort in Verlaufsform nach – im Hintergrund, der Cache passt weiter Token für Token.
- **Feste Schreibweise:** Die Antwort wird mit einer Grammatik (GBNF, `AnswerGrammar`) erzeugt. `{…}` gibt es nur als bekanntes Farb-Tag, ```` ``` ```` nur mit bekannter Sprache oder als Element mit vorgegebenem Zeilenformat. Geprüft wird nur das gezogene Token, nur bei einem ungültigen die ganze Auswahl. `MAX_GRAMMAR=0` schaltet sie ab.
- **Reparatur:** Elemente werden bis zum Blockende zurückgehalten (`ElementGate`) und mit derselben Logik geprüft, die sie zeichnet. Ist ein Block kaputt, geht die Engine auf den Stand nach der Kopfzeile zurück und erzeugt den Inhalt neu (Temperatur 0,3, höchstens zweimal), sonst wird er weggelassen.
- **Eingabezeile**: zuerst einfach, später mit Verlauf (↑/↓), mehrzeiliger Eingabe und Autovervollständigung für `/`-Befehle.
- **Farben**: eine feste, zurückhaltende Palette mit einer Akzentfarbe für Max (z. B. Cyan oder Bernstein).
- **Strg+C** bricht die laufende Antwort ab, beendet aber nicht Max.

### Befehle (Phase 1)

| Befehl | Funktion |
|---|---|
| `/help` | Befehle anzeigen |
| `/clear` | Gespräch zurücksetzen |
| `/debug` | Technische Infos: Modell, Backend, VRAM/RAM, Tokens/s, Kontextauslastung; schaltet außerdem einen Debug-Modus an/aus, der den Denk-Text und Timings zeigt |
| `/exit` | Beenden |
| *(optional)* `/save`, `/load` | Gespräch speichern bzw. laden |
| `/denken` | Nachdenken vor jeder Antwort an/aus |
| `/gedächtnis`, `/vergiss 3` bzw. `/vergiss alles` | Gedächtnis anzeigen bzw. Einträge löschen (siehe 8a) |

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
| 6 | `TierSelector` + Unit-Tests ✅ (später ersetzt durch `Requirements`: nur noch ein Modell) |
| 7 | `Manifest` – JSON laden, Fallback aus Embedded Resource ✅ |
| 8 | `ModelDownloader` mit Fortschrittsbalken, Fortsetzen, SHA-256 ✅ |
| 9 | Modellauswahl festlegen, Manifest befüllen ✅ |
| 10 | `LlmEngine` – Modell laden, Backend wählen, Antwort streamen ✅ |
| 11 | `Conversation` + `ChatSession` – Verlauf, Rollen, Kontext kürzen ✅ |
| 12 | System-Prompt einbinden und Persona testen ✅ |
| 13 | `ChatView` – Streaming-Ausgabe, Denk-Spinner, Strg+C bricht ab ✅ |
| 14 | `MarkdownRenderer` – eigener, streamender Renderer (Überschriften, Listen, Code-Blöcke, Tabellen, Zitate, Farb-Tags) ✅ |
| 14a | Syntax-Hervorhebung, Widgets (Diagramme, Baum, Kasten, Spalten, Kalender, Titel), Farbverlauf, `/demo` ✅ |
| 14b | Rückfragen mit Auswahlmenü (`frage`) ✅ |
| 14c | Sichtbares Nachdenken (grau, live, Denk-Budget, `/denken`), feste Schreibweise per Grammatik, unsichtbare Reparatur kaputter Elemente ✅ |
| 15 | `/debug`, `/clear`, Tokens pro Sekunde messen ✅ (Denk-Text-Schalter für `/debug` fehlt noch) |
| 16 | Eigene Eingabezeile: Einfügen ohne Abschicken, Shift/Alt+Enter und `\`+Enter für neue Zeilen, ↑/↓-Verlauf (gespeichert), Tab für Befehle ✅ |
| 17 | Publish: eine einzige Datei für `win-x64` und `linux-x64` (self-contained, llama.cpp-Bibliotheken eingebettet) ✅ |
| 18 | GitHub Action `release.yml`: Build bei Tag `v*`, Release + Manifest ✅ |
| 19 | Manifest und Release-Dateien laden (`ManifestSource`, `ModelDownloader` für beliebige Dateien) ✅ |
| 20 | `Updater` – stiller Hintergrund-Download von App und Modell, pausiert beim Schließen ✅ |
| 21 | `UpdateApplier` + `StartupUpdate` – Exe-Tausch per Umbenennen, Modell-Tausch beim Start, Pflicht-Update, Not-Aus, einmalige Nachricht ✅ |

**Meilenstein Phase 1:** `max.exe` weitergeben → beim ersten Start Einrichtung → danach sofort mit Max chatten, lokal und offline → neue Versionen kommen still von selbst.

---

## 8a. Gedächtnis & KI-Begrüßung (nach dem Chat-Kern)

Max soll sich merken, mit wem er spricht, und das nutzen, zum Beispiel für eine persönliche, kurze Begrüßung beim Start.

### Gedächtnis (`memory.json` im Datenordner)

```json
{
  "facts": [
    { "text": "Programmiert in C#.",       "added": "2026-09-25" },
    { "text": "Arbeitet oft spät abends.", "added": "2026-09-27" }
  ],
  "lastSession": { "ended": "2026-09-27T23:41:00", "summary": "Tabellen im Renderer repariert" },
  "nextGreeting": { "morning": "…", "day": "Zurück am Renderer?", "evening": "…", "night": "…", "created": "2026-09-27T23:41:00" }
}
```

- **Merken:** Beim Beenden (`/exit`, Strg+C, Eingabe-Ende) bekommt das Modell einen Extra-Auftrag hinter dem Gespräch (`Memory/Reflection.cs`): neue, dauerhafte Fakten über den Nutzer, eine kurze Zusammenfassung und vier Begrüßungen – ohne Nachdenken, in fester Form per Grammatik:
  ```
  FAKTEN:
  - Programmiert in C#.
  ZUSAMMENFASSUNG: Tabellen im Renderer repariert
  MORGEN: …   TAG: …   ABEND: …   NACHT: …
  ```
  Max zeigt dabei „Ich notiere mir noch kurz das Wichtigste …“; Strg+C überspringt es. Gab es kein Gespräch, passiert nichts.
- **Nutzen:** Die Fakten und das letzte Gespräch stehen als eigener Abschnitt ganz am Ende des System-Prompts (`## Was du über den Nutzer weißt`). Der feste Anfang bleibt dadurch gleich, und der gespeicherte Stand beim Start (Prompt-Cache) passt weiter.
- **Begrenzen:** Doppelte (gleich bis auf Groß-/Kleinschreibung und Satzzeichen) fallen weg, höchstens 8 neue Fakten pro Gespräch und 50 insgesamt – darüber fallen die ältesten weg. (Zusammenfassen durch das Modell erst, falls das in der Praxis nötig wird.)
- **Kontrolle:** `/gedächtnis` zeigt, was Max weiß; `/vergiss 3` löscht einen Eintrag, `/vergiss alles` alles – sofort, auch für das laufende Gespräch (neuer System-Prompt). Alles bleibt lokal auf dem Rechner.

### KI-Begrüßung

Problem: Das Modell zu laden dauert ein paar Sekunden, und die Begrüßung soll **sofort** da sein.

Lösung: **Die Begrüßung für den nächsten Start wird schon am Ende der aktuellen Sitzung erzeugt**, je eine für Morgen, Tag, Abend und Nacht; beim Start wird die passende gewählt.

- Im Kasten des Startbildschirms bleibt die kurze feste Begrüßung („Guten Abend, Alex.“) – die Spalte ist schmal.
- Darunter sagt Max die persönliche Begrüßung als ersten Satz im Chat („◆ Zurück am Renderer?“). Sie steht auch im Verlauf, damit eine Antwort darauf passt.
- Jede Begrüßung gilt nur einmal und höchstens 14 Tage; danach nur die feste.
- „Zuletzt“ im Startbildschirm zeigt das letzte Gespräch („Gestern, 23:41 – Tabellen im Renderer repariert“).
- Ein eigenes Interface `IGreetingProvider` war nicht nötig: `MemoryBook.TakeGreeting` liefert die Begrüßung oder nichts.

| # | Schritt |
|---|---|
| 22 | `MemoryStore` – `memory.json` laden und speichern, `/gedächtnis`, `/vergiss` ✅ |
| 23 | Fakten-Extraktion am Sitzungsende + Einbau in den System-Prompt ✅ |
| 24 | KI-Begrüßung (vorab erzeugt, je Tageszeit) mit Fallback, „Zuletzt“ im Startbildschirm ✅ |

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

## 9b. Spezialisten (Idee, nach der Tool-Schnittstelle)

Das 9B-Modell bleibt das Gesprächsmodell. Manche Tools haben statt festem Code ein **kleines Spezialmodell** dahinter – für Max ist das ein Tool wie jedes andere (`bild_beschreiben`, `audio_abschreiben`, `bild_erstellen` …). Ein Spezialist kommt nur dazu, wenn er das 9B-Modell in seinem Gebiet nachweislich schlägt.

| Spezialist | Ansatz | Bemerkung |
|---|---|---|
| Bilder und Screenshots lesen | Vision-Zusatz von Qwen3.5 (`mmproj`), falls vorhanden | kein zweites Modell nötig; LLamaSharp kann das (mtmd) |
| Sprache → Text | Whisper (whisper.cpp / Whisper.net) | klein, sehr gut, lokal |
| Bilder erzeugen | kleines Diffusionsmodell über stable-diffusion.cpp | eigene Laufzeit; Anzeige im Terminal (Kitty/Sixel) oder als Datei |
| Mathe | Spezialmodell nur bei klarem Vorsprung | exaktes Rechnen besser über ein Rechen-/Python-Tool |
| Recherche | hängt vor allem an Websuche und Seiten lesen (Tools) | ein kleines Modell höchstens zum Zusammenfassen |

**Technik:**
- Ein `SpecialistManager` lädt Spezialisten erst bei Bedarf und entlädt sie danach – neben dem 9B-Modell ist auf der Grafikkarte wenig Platz.
- Die Dateien stehen als eigene Einträge im Manifest und kommen über den Hintergrund-Download (Schritt 20) erst, wenn der Spezialist zum ersten Mal gebraucht wird.

## 9a. Eigener Max-Adapter (Fine-Tuning, nach Phase 2)

Statt Max' Persönlichkeit nur über den System-Prompt vorzugeben, wird sie dem Modell mit einem **LoRA-Adapter** antrainiert. Ein LoRA-Adapter ist ein kleiner Zusatz mit wenigen Millionen Werten auf dem fertigen Modell; das Modell selbst wird nicht neu trainiert.

**Warum erst nach Phase 2:** Dann steht fest, wie Tool-Aufrufe aussehen. Persönlichkeit und Tool-Format werden in einem Rutsch trainiert.

**Was es bringt:**
- Die Persönlichkeit ist fest eingebaut: Ton, Duzen, kein Verraten des Modells, gute Formatierung mit Markdown und Farb-Tags.
- Der System-Prompt wird deutlich kürzer. Das Aufwärmen beim Start geht dadurch schneller, und im Kontext bleibt mehr Platz.
- Tool-Aufrufe klappen zuverlässiger.

**Was es nicht bringt:** neues Wissen oder mehr Grundintelligenz. Wissen kommt weiter über Gedächtnis (8a) und Tools (Phase 2).

**Ablauf:**
1. **Datensatz:** 1.000–3.000 Beispiel-Gespräche im Max-Stil, darunter Smalltalk, Erklärungen, Code, Tabellen, Fragen nach der Identität, ernste Themen und Tool-Aufrufe.
   - Die Beispiele lassen sich zum Teil mit einem großen Modell erzeugen. Danach werden sie von Hand geprüft und aussortiert.
   - Der Datensatz liegt im Repo unter `training/` als JSONL.
2. **Training:** QLoRA mit **Unsloth** auf den Originalgewichten von Hugging Face, also nicht auf der GGUF-Datei.
   - 9B braucht für QLoRA eine Grafikkarte mit reichlich Speicher; sonst eine gemietete Cloud-GPU oder Google Colab.
   - Ein Adapter passt nur zu seinem Grundmodell – mit einem einzigen Modell gibt es auch nur einen Adapter.
3. **Umwandeln:** Den Adapter mit llama.cpp nach GGUF konvertieren (`convert_lora_to_gguf.py`), etwa 50–100 MB.
4. **Einbinden:** Das Manifest bekommt beim Modell ein Feld `adapter` (URL, SHA-256, Revision).
   - Max lädt den Adapter wie das Modell und hängt ihn beim Laden an; LLamaSharp kann LoRA-Adapter laden.
   - Neue Adapter-Versionen kommen über die stillen Updates (Abschnitt 5a).
5. **Prüfen:** Der Selbsttest-Workflow vergleicht die Antworten mit und ohne Adapter: Ton, Anrede, verratene Herkunft, Format der Tool-Aufrufe.

| # | Schritt |
|---|---|
| 25 | Datensatz-Format festlegen, erste 200 Beispiele, Skript zum Erzeugen und Prüfen |
| 26 | Training mit Unsloth für das 9B-Modell, Vergleich im Selbsttest |
| 27 | Adapter im Manifest, Laden in `LlmEngine`, kürzerer System-Prompt |
| 28 | Stilles Update des Adapters |

---

## 10. Offene Punkte

- [x] **Aufwärmen zwischenspeichern:** Auf Rechnern ohne Grafikkarte dauert das Aufwärmen mit dem langen System-Prompt lange (4B-Modell auf 4 Kernen: ca. 90 s, danach 2,5 s). Lösung: den aufgewärmten Zustand mit `LLamaContext.SaveState` im Datenordner speichern. Der Schlüssel ist eine Prüfsumme aus Prompt und Modell; beim nächsten Start wird der Zustand in etwa einer Sekunde geladen.
- [x] **Test auf einem Rechner mit Grafikkarte:** Grafikkarte mit 12 GB über Vulkan – alle 32 Schichten auf der Karte, **49 Tokens/s**, erstes Token nach 0,3 s, Nachdenken ~6 s (280 Tokens), Aufwärmen aus dem Zwischenspeicher 1,4 s, Laden 5 s.
- [x] Ist Vulkan auf NVIDIA spürbar langsamer als CUDA? Nicht nötig: Mit 49 Tokens/s ist Vulkan schnell genug, CUDA bleibt draußen.
- [ ] **Vor dem ersten Release `v0.2.0`: fertige `max.exe` auf einem Rechner mit Grafikkarte testen.** Bisher lief die Grafikkarte nur mit `dotnet run`; die einzelne Datei nur auf GitHub ohne Grafikkarte. Ablauf: Selbsttest-Workflow mit `windows-latest` und „exe“ starten, `max-Windows` unter „Artifacts“ herunterladen, starten, in `/debug` auf „32/32 Schichten auf GPU“ achten. Dabei die SmartScreen-Warnung ansehen (Datei ist nicht signiert) und entscheiden, ob das für den Anfang reicht. Danach `git tag v0.2.0 && git push origin v0.2.0`.
- [ ] Repo auf öffentlich stellen und Zwei-Faktor-Anmeldung auf GitHub prüfen.
- [x] Soll ein Pflicht-Update auch einen „Wartungsmodus“ bekommen (Max per Manifest komplett sperren)? Ja: `disabled` + `message` im Manifest.
- [ ] Akzentfarbe und Banner-Design festlegen.
