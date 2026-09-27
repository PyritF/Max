# Max

Ein lokaler KI-Assistent fürs Terminal – ohne Cloud, ohne API-Key.

- Plan & Architektur: [PLAN.md](PLAN.md)

## Bauen & Starten

```bash
dotnet build            # alles bauen
dotnet test             # Tests ausführen
dotnet run --project src/Max
dotnet run --project src/Max -- --demo-first-start   # Vorschau: erster Start mit Download
```

Beim ersten Start richtet Max sich ein und lädt dabei das Modell herunter (rund 5,7 GB).

**Voraussetzungen:** eine Grafikkarte mit mindestens 6 GB Speicher (ab 8 GB passt das Modell ganz darauf)
und mindestens 8 GB Arbeitsspeicher. Fehlt etwas, sagt Max das beim Start und lädt nichts herunter.

### Schalter für Entwickler

| Umgebungsvariable | Wirkung |
|---|---|
| `MAX_HOME` | anderer Datenordner, z. B. zum Testen der Einrichtung |
| `MAX_CPU` | `1` erlaubt den Start ohne Grafikkarte (langsam) – für Tests, oder falls die Karte nicht erkannt wird |
| `MAX_MANIFEST_URL` | Manifest von einer anderen Adresse laden |
| `MAX_GRAMMAR` | `0` schaltet die feste Schreibweise (Grammatik) ab – zur Fehlersuche |

```powershell
$env:MAX_HOME = "$env:TEMP\max-test"; dotnet run --project src/Max
```

### Selbsttest

`max --selftest` stellt nach dem Start ein paar feste Fragen und gibt Antworten und Tokens pro Sekunde aus.
Auf GitHub läuft das als Workflow **Selbsttest** (Actions → Selbsttest → Run workflow) – mit echtem Download von Hugging Face, auf der CPU (die Test-Rechner haben keine Grafikkarte).

In Visual Studio: `Max.slnx` öffnen, `Max` als Startprojekt, F5.
