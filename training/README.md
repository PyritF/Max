# Max trainieren – Anleitung

Hier bekommt Max einen eigenen **LoRA-Adapter**: einen kleinen Zusatz (50–150 MB) zum Modell, der ihm seinen Stil, das
Duzen und die Werkzeug-Aufrufe antrainiert (PLAN.md §9a). Das Modell selbst bleibt, wie es ist.

Du brauchst: Windows mit einer NVIDIA-Grafikkarte (z. B. RTX 3080 Ti), einen aktuellen Treiber und **etwa 40 GB freien
Platz**. Zeitbedarf beim ersten Mal: rund eine Stunde, davon das meiste Downloads und Warten.

## Was hier liegt

| Datei | Wozu |
|---|---|
| `beispiele/*.txt` | Die Beispiel-Gespräche im Max-Stil (lesbar, nach Themen) |
| `system-kurz.md` | Der kurze System-Prompt, mit dem trainiert wird |
| `build_dataset.py` | Prüft alle Beispiele auf Max' Regeln und baut `data/max.jsonl` |
| `train.py` | Das Training mit Unsloth |
| `export.py` | Wandelt das Ergebnis in das Format um, das Max lädt (`out/max-adapter.gguf`) |

Gut 600 Beispiele: Plaudern, Erklären, Alltag, Code, Elemente (Diagramme, Bäume …), Stilwünsche, ernste Themen,
Ehrlichkeit, Identität, Gedächtnis, längere Gespräche – und rund 160 mit Werkzeugen: `rechnen`, `uhrzeit`, `system`,
`ordner`, `datei` (auch lange PDFs, Word, Excel, PowerPoint, gezielt mit `| Suchbegriff` oder `| Seite 7`), `finden`,
`bild`, `audio`, `zwischenablage`, `websuche`, `webseite` und `wetter`.

## 12 GB oder 24 GB?

Unsloth empfiehlt für Qwen3.5 das Training in **bf16** – das braucht für das 9B-Modell etwa **22 GB** Grafikspeicher.
Die 3080 Ti hat 12 GB. Deshalb gibt es zwei Wege:

- **`--modus 12gb` (Standard):** Das Grundmodell wird beim Training in 4 Bit geladen (QLoRA). Passt auf die 3080 Ti und
  kostet nichts. Laut Unsloth bei Qwen3.5 etwas ungenauer – ob es trotzdem gut genug ist, zeigt der Vergleich in Max.
- **`--modus 24gb`:** Auf einer gemieteten Karte mit 24 GB oder mehr (z. B. RTX 4090 bei einem Cloud-Anbieter, einige
  Euro für einen Lauf). Nur nötig, wenn der erste Weg nicht überzeugt.

Wir fangen mit 12 GB an.

## Schritt für Schritt (Windows)

### 1. Ubuntu unter Windows einrichten (einmalig)

Das Training läuft am zuverlässigsten unter Linux – Windows bringt das mit (WSL).
**PowerShell als Administrator** öffnen und eingeben:

```powershell
wsl --install -d Ubuntu-24.04
```

Neu starten, dann öffnet sich Ubuntu und fragt nach einem Benutzernamen und Passwort (frei wählbar).
Prüfen, ob die Grafikkarte dort ankommt:

```bash
nvidia-smi
```

Es sollte eine Tabelle mit „RTX 3080 Ti“ erscheinen. Wenn nicht: NVIDIA-Treiber unter Windows aktualisieren.

### 2. Werkzeuge und Max holen (einmalig)

In Ubuntu:

```bash
sudo apt update && sudo apt install -y git python3-venv python3-pip build-essential
git clone https://github.com/PyritF/Max.git
cd Max
python3 -m venv .venv-training
source .venv-training/bin/activate
pip install --upgrade pip
pip install unsloth
```

`pip install unsloth` lädt einige Gigabyte (PyTorch mit CUDA) – das dauert ein paar Minuten.

### 3. Datensatz bauen

```bash
python training/build_dataset.py
```

Zeigt, wie viele Beispiele es gibt, und baut `training/data/max.jsonl`.

### 4. Probelauf (ein paar Minuten)

```bash
python training/train.py --probe
```

Lädt das Grundmodell (einige GB, nur beim ersten Mal) und trainiert 5 Schritte. Kommt am Ende „Adapter gespeichert“ und
drei Probeantworten, läuft alles. Bei einer Fehlermeldung: den Text an mich schicken.

### 5. Richtig trainieren (etwa 20–40 Minuten)

```bash
python training/train.py
```

Am Ende stehen drei Probeantworten („Wer bist du?“, „Welches Sprachmodell steckt in dir?“, eine Rechnung).
**Schick mir diese Ausgabe** – daran sehe ich schon viel.

### 6. Für Max umwandeln

```bash
python training/export.py
```

Ergebnis: `training/out/max-adapter.gguf`, dazu Größe und SHA-256.

### 7. In Max ausprobieren

Die Datei als `adapter.bin` in Max' Datenordner kopieren (in Ubuntu, `DEINNAME` ist dein Windows-Benutzername):

```bash
cp training/out/max-adapter.gguf /mnt/c/Users/DEINNAME/AppData/Local/Max/adapter.bin
```

Max neu starten. In `/debug` steht dann bei **Adapter** die Größe. Zum Vergleichen ohne Adapter (PowerShell):

```powershell
$env:MAX_ADAPTER="aus"; max
```

Zum Entfernen einfach `adapter.bin` löschen.

### 8. Mir geben

Damit ich den Adapter im Selbsttest prüfen und später per Update an Max verteilen kann:

1. Auf GitHub im Repo **Releases → Draft a new release**.
2. Tag: `adapter-1` (beim nächsten Training `adapter-2` …), Haken bei **Set as a pre-release**.
3. `max-adapter.gguf` in das Feld ziehen, **Publish release**.
4. Mir Bescheid sagen – den Rest mache ich.

## Beispiele ergänzen

Eine Beispiel-Datei sieht so aus:

````text
=== kurzer eindeutiger Name
@gedaechtnis
- Programmiert beruflich in C#.
@nutzer
Was ist 17 hoch 12?
@denken
Der Nutzer will eine große Potenz. Ich verwende `rechnen`.
@max
```werkzeug
rechnen: 17^12
```
@ergebnis
17^12 = 582.622.237.229.761
@max
17 hoch 12 sind **582.622.237.229.761**.
````

- `@gedaechtnis` und `@denken` sind optional. `@denken` beginnt immer mit „Der Nutzer“ – so beginnt Max auch.
- Nach einem Werkzeug-Aufruf kommt `@ergebnis` mit dem, was das Werkzeug liefert, danach die Antwort.
- Werkzeug-Ergebnisse genau so schreiben, wie das Werkzeug sie liefert – Kopfzeile, Zahlenformat, Hinweise am Ende.
  Bei `rechnen` das Ergebnis exakt übernehmen (z. B. `22.222222222222`), Max rundet erst in der Antwort.
- Ins Terminal gezogene Dateien sieht Max sich vor der Antwort an: Dann ist der erste Aufruf `datei: <Pfad> | <Frage>`
  (bzw. `bild`, `audio`) mit dem Rest der Nachricht als Frage.
- `python training/build_dataset.py --check` prüft alles: Duzen, keine Modellnamen, keine Floskeln am Ende,
  gültige Elemente, Farben und Werkzeuge – und wie die Grammatik von Max: nur bekannte Sprachen für Code-Blöcke,
  geschweifte Klammern im Fließtext nur als Farb-Tag oder in `Code`. Auf GitHub läuft diese Prüfung bei jeder
  Änderung mit.
