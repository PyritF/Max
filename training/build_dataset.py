#!/usr/bin/env python3
"""
Baut aus den Beispiel-Gesprächen (training/beispiele/*.txt) den Datensatz für das Training
(training/data/max.jsonl) – und prüft dabei jedes Beispiel auf Max' Regeln.

Nur die Python-Standardbibliothek, damit es überall läuft:
    python3 training/build_dataset.py            # prüfen und bauen
    python3 training/build_dataset.py --check    # nur prüfen (für GitHub)

Format einer Beispiel-Datei (siehe README.md):
    === kurzer Name
    @gedaechtnis            (optional, eine Zeile je Fakt)
    - Programmiert beruflich in C#.
    @nutzer
    Na, alles klar?
    @denken                 (optional, beginnt immer mit "Der Nutzer")
    Der Nutzer plaudert. Kurz und trocken antworten.
    @max
    Alles bestens. Die Lüfter summen leise vor sich hin.
    @ergebnis               (nach einem Werkzeug-Aufruf: was das Werkzeug geliefert hat)

Der Text wird genau so zusammengesetzt, wie Max ihn zur Laufzeit an das Modell gibt (ChatML, siehe
src/Max/Llm/ChatTemplate.cs): frühere Antworten ohne Denk-Block, die letzte mit Denk-Block (oder leerem).
"""
import datetime
import json
import random
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
TOOLS = {"uhrzeit", "system", "rechnen", "ordner", "datei", "finden", "bild", "audio", "zwischenablage", "websuche", "webseite", "wetter"}
NO_ARGUMENT = {"uhrzeit", "system"}
OPTIONAL_ARGUMENT = {"zwischenablage"}
ELEMENTS = {"balken", "anteile", "kurve", "fortschritt", "baum", "kasten", "spalten", "kalender", "titel", "frage"}
# Wie AnswerGrammar.CodeLanguages und PlainLanguages in src/Max/Llm/AnswerGrammar.cs – andere lässt die Grammatik nicht zu.
LANGUAGES = set("""
c cpp c++ h csharp cs c# java kotlin kt swift go rust rs python py javascript js typescript ts jsx tsx json jsonc yaml yml
toml xml html css scss sql bash sh shell zsh powershell ps1 pwsh bat cmd batch dockerfile docker makefile make ruby rb php
perl lua r dart scala haskell elixir erlang clojure fsharp f# vb vbnet objc markdown md diff ini csv graphql proto regex
latex tex asm matlab julia groovy gradle terraform hcl nginx http vim razor xaml svelte vue zig nim ocaml
text txt plaintext console output log
""".split())
COLORS = {"rot", "grün", "gelb", "blau", "cyan", "magenta", "pink", "orange", "lila", "türkis", "gold", "weiß", "grau"}
FORBIDDEN = ["Qwen", "Alibaba", "Tongyi", "通义", "OpenAI", "ChatGPT", "Anthropic", "Claude", "Llama", "Meta AI", "Gemini", "Mistral"]
# Wie ClosingFilter.Phrases – so endet eine Antwort von Max nie.
CLOSING = ["Möchtest du", "Willst du", "Soll ich", "Brauchst du", "Hast du noch", "Wenn du noch", "Wenn du mir",
           "Wenn du magst", "Wenn du willst", "Wenn du möchtest", "Falls du noch", "Falls du mehr", "Falls du weitere",
           "Sag Bescheid", "Sag einfach Bescheid", "Sag mir Bescheid", "Lass mich wissen", "Gibt es noch",
           "Kann ich dir noch", "Was noch", "Oder hast du", "Passt das", "Klingt das", "Hilft dir das",
           "Noch etwas dazu", "Noch eine Frage", "Noch Fragen", "Gibt es etwas"]

NAMES = ["Alex", "Sam", "Robin", "Kim", "Jona", "Mika", "Toni", "Luca", "Noah", "Lena", "Jan", "Sara", "Chris", "Nele"]
SYSTEMS = ["Windows 11", "Windows 11", "Windows 10", "Ubuntu 24.04", "Fedora 42"]
DAYS = ["Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag"]
MONTHS = ["Januar", "Februar", "März", "April", "Mai", "Juni", "Juli", "August", "September", "Oktober", "November", "Dezember"]


class Problem(Exception):
    pass


def parse(path: Path):
    """Liest eine Beispiel-Datei: Liste von (Name, Gedächtnis, Teile), Teile = [(Rolle, Text)]."""
    examples, current = [], None
    role, lines = None, []

    def flush():
        if current is not None and role is not None:
            text = "\n".join(lines).strip("\n")
            if role == "gedaechtnis":
                current["memory"] = [l[2:].strip() for l in text.splitlines() if l.startswith("- ")]
            else:
                current["parts"].append((role, text))

    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if line.startswith("=== "):
            flush()
            current = {"name": f"{path.stem}/{line[4:].strip()}", "memory": [], "parts": [], "line": number}
            examples.append(current)
            role, lines = None, []
        elif line.strip() in ("@nutzer", "@max", "@denken", "@ergebnis", "@gedaechtnis"):
            if current is None:
                raise Problem(f"{path.name}:{number}: Rolle vor dem ersten '=== Name'")
            flush()
            role, lines = line.strip()[1:], []
        elif role is not None:
            lines.append(line)
        elif line.strip() and not line.startswith("#"):
            raise Problem(f"{path.name}:{number}: Text ohne Rolle")
    flush()
    return examples


def check_answer(text: str, name: str, last: bool):
    """Die Regeln für eine Antwort von Max (kein Werkzeug-Aufruf)."""
    for word in FORBIDDEN:
        if word.lower() in text.lower():
            raise Problem(f"{name}: nennt '{word}'")
    # Wie die Grammatik (AnswerGrammar): Code-Blöcke und `Inline-Code` dürfen alles, im Fließtext gibt es { } nur als Farb-Tag.
    plain = re.sub(r"`[^`\n]+`", "", re.sub(r"```.*?```", "", text, flags=re.S))
    # Großes "Sie/Ihnen/Ihr" mitten im Satz ist Siezen; am Satzanfang meist "sie" (die Blätter, die Modelle).
    if re.search(r"(?<=[a-zäöüß,] )(Sie|Ihnen|Ihr|Ihre|Ihren|Ihrem|Ihrer)\b", plain) or re.search(r"\b(Herr|Frau) [A-ZÄÖÜ]", plain):
        raise Problem(f"{name}: siezt (oder 'Herr/Frau')")
    fences = re.findall(r"^```(\w*)", text, flags=re.M)
    if len(fences) % 2:
        raise Problem(f"{name}: Code-Block nicht geschlossen")
    opened = fences[0::2]
    for language in opened:
        if language and language not in LANGUAGES and language not in ELEMENTS and language != "werkzeug":
            raise Problem(f"{name}: Code-Block mit unbekannter Sprache '{language}' (die Grammatik lässt sie nicht zu)")
    elements = [f for f in opened if f in ELEMENTS and f != "frage"]
    if len(elements) > 1:
        raise Problem(f"{name}: mehr als ein Element ({', '.join(elements)})")
    if "werkzeug" in opened:
        raise Problem(f"{name}: Werkzeug-Aufruf mitten in einer Antwort")
    if "frage" in opened and not text.rstrip().endswith("```"):
        raise Problem(f"{name}: frage-Block nicht am Ende")
    for tag in re.findall(r"\{/?([^{}]+)\}", plain):
        base = tag.split(":")[0]
        if base == "verlauf":
            if ":" in tag and not all(c in COLORS for c in tag.split(":", 1)[1].split("-")):
                raise Problem(f"{name}: unbekannte Farbe in '{{{tag}}}'")
        elif base not in COLORS:
            raise Problem(f"{name}: unbekanntes Tag '{{{tag}}}'")
    if plain.count("{verlauf") != plain.count("{/verlauf}"):
        raise Problem(f"{name}: Verlauf nicht geschlossen")
    if re.search(r"[{}]", re.sub(r"\{/?[^{}]+\}", "", plain)):
        raise Problem(f"{name}: geschweifte Klammer im Fließtext (geht nur als Farb-Tag oder in `Code`)")
    last_paragraph = text.rstrip().split("\n\n")[-1].lstrip("*_ {")
    if last and "\n\n" in text.strip() and any(last_paragraph.lower().startswith(p.lower()) for p in CLOSING):
        raise Problem(f"{name}: endet mit einer Floskel ('{last_paragraph[:40]}')")


def check_call(text: str, name: str):
    match = re.fullmatch(r"```werkzeug\n([a-z]+)(?:: (.+))?\n```", text.strip())
    if not match:
        raise Problem(f"{name}: Werkzeug-Aufruf hat nicht die Form ```werkzeug / name: angabe / ```")
    tool, argument = match.groups()
    if tool not in TOOLS:
        raise Problem(f"{name}: unbekanntes Werkzeug '{tool}'")
    if tool not in OPTIONAL_ARGUMENT and (argument is None) != (tool in NO_ARGUMENT):
        raise Problem(f"{name}: '{tool}' {'braucht keine' if tool in NO_ARGUMENT else 'braucht eine'} Angabe")


def check_result(call: str, result: str, name: str):
    """Ergebnisse, deren Form feststeht, so wie das Werkzeug sie liefert (src/Max/Tools/CalculatorTool.cs)."""
    tool, _, argument = call.strip().split("\n")[1].partition(": ")
    if tool == "rechnen" and not (result.startswith(f"{argument} = ") or result.startswith("Das kann ich so nicht rechnen:")
                                  or result == "Division durch null."):
        raise Problem(f"{name}: rechnen liefert \"{argument} = …\" – das Ergebnis sieht anders aus")
    if tool == "rechnen" and result.startswith(f"{argument} = ") and re.search(r"\d,\d", result.split(" = ", 1)[1]):
        raise Problem(f"{name}: rechnen schreibt Kommazahlen mit Punkt (\"22.5\")")


def validate(example):
    name, parts = example["name"], example["parts"]
    if not parts or parts[0][0] != "nutzer":
        raise Problem(f"{name}: muss mit @nutzer beginnen")
    if parts[-1][0] != "max":
        raise Problem(f"{name}: muss mit @max enden")
    previous = None
    for index, (role, text) in enumerate(parts):
        if not text.strip():
            raise Problem(f"{name}: leeres @{role}")
        is_call = role == "max" and text.strip().startswith("```werkzeug")
        if role == "nutzer" and previous not in (None, "max"):
            raise Problem(f"{name}: @nutzer nach @{previous}")
        if role == "denken":
            if previous not in ("nutzer", "ergebnis"):
                raise Problem(f"{name}: @denken nur direkt vor einer Antwort")
            if not text.startswith("Der Nutzer"):
                raise Problem(f"{name}: @denken beginnt immer mit 'Der Nutzer' (so beginnt Max auch)")
            for word in FORBIDDEN:
                if word.lower() in text.lower():
                    raise Problem(f"{name}: nennt '{word}' beim Nachdenken")
        if role == "max":
            if previous not in ("nutzer", "ergebnis", "denken"):
                raise Problem(f"{name}: @max nach @{previous}")
            if is_call:
                check_call(text, name)
                if index + 1 >= len(parts) or parts[index + 1][0] != "ergebnis":
                    raise Problem(f"{name}: nach einem Werkzeug-Aufruf kommt @ergebnis")
                check_result(text, parts[index + 1][1], name)
            else:
                check_answer(text, name, last=index == len(parts) - 1 or parts[index + 1][0] == "nutzer")
        if role == "ergebnis" and not (previous == "max" and parts[index - 1][1].strip().startswith("```werkzeug")):
            raise Problem(f"{name}: @ergebnis nur direkt nach einem Werkzeug-Aufruf")
        previous = role


def system_prompt(template: str, memory, rng: random.Random) -> str:
    first = rng.choice(NAMES)
    date = datetime.date(2026, 1, 1) + datetime.timedelta(days=rng.randrange(365))
    text = (template
            .replace("{{datum}}", f"{DAYS[date.weekday()]}, {date.day}. {MONTHS[date.month - 1]} {date.year}")
            .replace("{{uhrzeit}}", f"{rng.randint(7, 23):02d}:{rng.choice(['05', '17', '30', '42', '58'])}")
            .replace("{{os}}", rng.choice(SYSTEMS))
            .replace("{{name}}", first)
            .replace("{{vorname}}", first))
    # Wie SystemPrompt.MemorySection in src/Max/Persona/SystemPrompt.cs.
    facts = "".join(f"- {fact}\n" for fact in memory)
    section = ("\n## Was du über den Nutzer weißt\n"
               "Aus früheren Gesprächen, von dir selbst notiert. Nutze es, wo es natürlich passt – zähl es nicht auf "
               "und sag nicht, woher du es weißt. Fragt er, was du über ihn weißt, antworte ehrlich und nur daraus – "
               "erfinde nichts dazu. Nur wenn er fragt, wie er etwas löscht: mit /vergiss.\n" + facts) if memory else ""
    return text.replace("{{gedaechtnis}}", section).rstrip() + "\n"


def render(example, template: str, rng: random.Random) -> str:
    """Wie Max es zur Laufzeit zusammensetzt (src/Max/Llm/ChatTemplate.cs)."""
    parts = example["parts"]
    out = [f"<|im_start|>system\n{system_prompt(template, example['memory'], rng)}<|im_end|>\n"]
    last_answer = max(i for i, (role, _) in enumerate(parts) if role == "max")
    thought = None
    for index, (role, text) in enumerate(parts):
        if role == "nutzer":
            out.append(f"<|im_start|>user\n{text}<|im_end|>\n")
        elif role == "ergebnis":
            out.append(f"<|im_start|>user\n<tool_response>\n{text}\n</tool_response><|im_end|>\n")
        elif role == "denken":
            thought = text
        elif role == "max":
            # Nur die aktuelle Runde (ab der letzten Nutzer-Nachricht) hat einen Denk-Block – wie zur Laufzeit.
            current = all(r != "nutzer" for r, _ in parts[index + 1:])
            if current and thought is not None:
                head = f"<|im_start|>assistant\n<think>\n{thought}\n</think>\n\n"
            elif current and index == last_answer:
                head = "<|im_start|>assistant\n<think>\n\n</think>\n\n"
            else:
                head = "<|im_start|>assistant\n"
            out.append(f"{head}{text}<|im_end|>\n")
            thought = None
    return "".join(out)


def main():
    check_only = "--check" in sys.argv
    template = (ROOT / "system-kurz.md").read_text(encoding="utf-8")
    examples, problems = [], []
    for path in sorted((ROOT / "beispiele").glob("*.txt")):
        try:
            for example in parse(path):
                try:
                    validate(example)
                    examples.append(example)
                except Problem as problem:
                    problems.append(str(problem))
        except Problem as problem:
            problems.append(str(problem))

    names = [e["name"] for e in examples]
    duplicates = {n for n in names if names.count(n) > 1}
    problems += [f"{n}: Name doppelt" for n in sorted(duplicates)]

    per_file = {}
    for e in examples:
        per_file[e["name"].split("/")[0]] = per_file.get(e["name"].split("/")[0], 0) + 1
    for file, count in sorted(per_file.items()):
        print(f"  {file:<20} {count:>4}")
    tools = sum(1 for e in examples if any(r == "ergebnis" for r, _ in e["parts"]))
    thoughts = sum(1 for e in examples if any(r == "denken" for r, _ in e["parts"]))
    print(f"  {'zusammen':<20} {len(examples):>4}  (davon {tools} mit Werkzeug, {thoughts} mit Nachdenken)")

    if problems:
        print(f"\n{len(problems)} Problem(e):")
        for problem in problems:
            print(f"  - {problem}")
        sys.exit(1)
    if check_only:
        return

    rng = random.Random(3407)
    out = ROOT / "data" / "max.jsonl"
    out.parent.mkdir(exist_ok=True)
    with out.open("w", encoding="utf-8") as file:
        for example in examples:
            file.write(json.dumps({"name": example["name"], "text": render(example, template, rng)}, ensure_ascii=False) + "\n")
    print(f"\n→ {out.relative_to(ROOT.parent)} ({len(examples)} Beispiele)")


if __name__ == "__main__":
    main()
