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

Beim ersten Start richtet Max sich ein und lädt dabei das passende Modell herunter (1–21 GB, je nach Rechner).

### Schalter für Entwickler

| Umgebungsvariable | Wirkung |
|---|---|
| `MAX_HOME` | anderer Datenordner, z. B. zum Testen der Einrichtung |
| `MAX_TIER` | Stufe erzwingen: `S`, `M`, `L` oder `XL` |
| `MAX_MANIFEST_URL` | Manifest von einer anderen Adresse laden |

```powershell
$env:MAX_HOME = "$env:TEMP\max-test"; $env:MAX_TIER = "S"; dotnet run --project src/Max
```

### Selbsttest

`max --selftest` stellt nach dem Start ein paar feste Fragen und gibt Antworten und Tokens pro Sekunde aus.
Auf GitHub läuft das als Workflow **Selbsttest** (Actions → Selbsttest → Run workflow) – mit echtem Download von Hugging Face.

In Visual Studio: `Max.slnx` öffnen, `Max` als Startprojekt, F5.
