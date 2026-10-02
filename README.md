# Max

Ein lokaler KI-Assistent fürs Terminal – ohne Cloud, ohne API-Key.

- Plan & Architektur: [PLAN.md](PLAN.md)

## Was Max kann

- Plaudern, erklären, Code schreiben – mit Diagrammen, Tabellen und Farben direkt im Terminal.
- Dateien lesen: Text und Code, PDF, Word, Excel, PowerPoint – auch lange Dokumente gezielt an der richtigen Stelle,
  eingescannte Seiten über den Bild-Zusatz.
- Dateien finden, Bilder und Screenshots ansehen (auch aus der Zwischenablage), Aufnahmen abschreiben.
- Im Web suchen, Webseiten lesen, das Wetter nachsehen, exakt rechnen.
- Sich Wichtiges über dich merken und Gespräche fortsetzen (`/verlauf`, `/weiter`).
- Alles nur lesend: Max verändert nichts auf deinem Rechner. Ins Netz geht nur, was er nachschlägt.

## Bauen & Starten

```bash
dotnet build            # alles bauen
dotnet test             # Tests ausführen
dotnet run --project src/Max
dotnet run --project src/Max -- --demo-first-start   # Vorschau: erster Start mit Download
```

Beim ersten Start richtet Max sich ein und lädt dabei das Modell herunter (rund 5,7 GB). Der Bild-Zusatz (0,9 GB) und
die Spracherkennung (0,6 GB) kommen danach still im Hintergrund.

**Voraussetzungen:** eine Grafikkarte mit mindestens 4 GB Speicher (ab 8 GB passt das Modell ganz darauf; unter 6 GB rechnet der Prozessor mit, dann braucht es 16 GB Arbeitsspeicher)
und mindestens 8 GB Arbeitsspeicher. Fehlt etwas, sagt Max das beim Start und lädt nichts herunter.

### Schalter für Entwickler

| Umgebungsvariable | Wirkung |
|---|---|
| `MAX_HOME` | anderer Datenordner, z. B. zum Testen der Einrichtung |
| `MAX_CPU` | `1` erlaubt den Start ohne Grafikkarte (langsam) – für Tests, oder falls die Karte nicht erkannt wird |
| `MAX_MANIFEST_URL` | Manifest von einer anderen Adresse laden |
| `MAX_RELEASE_URL` | Updates von einer anderen Adresse laden (statt `github.com/PyritF/Max/releases/download`) |
| `MAX_NO_UPDATE` | `1` schaltet die stillen Updates ab |
| `MAX_GRAMMAR` | `0` schaltet die feste Schreibweise (Grammatik) ab – zur Fehlersuche |
| `MAX_ADAPTER` | `aus` lädt Max ohne den eigenen Adapter (`adapter.bin`) – zum Vergleichen |

```powershell
$env:MAX_HOME = "$env:TEMP\max-test"; dotnet run --project src/Max
```

### Neue Version veröffentlichen

```bash
git tag v0.2.0 && git push origin v0.2.0
```

Der Workflow **Release** baut `max.exe` (Windows) und `max` (Linux) als je eine einzige Datei – ohne dass
.NET installiert sein muss –, legt ein GitHub-Release an und trägt Version und Prüfsummen in `manifest.json` ein.
Jedes installierte Max merkt das beim nächsten Start, lädt die neue Version still im Hintergrund und nutzt sie
ab dem Start danach. Einzelne Datei lokal bauen: `dotnet publish src/Max -c Release -r win-x64` (bzw. `linux-x64`).

### Selbsttest

`max --selftest` stellt nach dem Start ein paar feste Fragen und gibt Antworten und Tokens pro Sekunde aus.
Auf GitHub läuft das als Workflow **Selbsttest** (Actions → Selbsttest → Run workflow) – mit echtem Download von Hugging Face, auf der CPU (die Test-Rechner haben keine Grafikkarte). Mit dem Häkchen „exe“ wird dabei die fertige einzelne Datei getestet.

In Visual Studio: `Max.slnx` öffnen, `Max` als Startprojekt, F5.
