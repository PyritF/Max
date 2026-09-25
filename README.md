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

In Visual Studio: `Max.slnx` öffnen, `Max` als Startprojekt, F5.
