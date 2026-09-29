#!/usr/bin/env python3
"""
Trainiert Max' LoRA-Adapter auf dem Grundmodell (Qwen3.5-9B) mit Unsloth – auf deiner eigenen Grafikkarte.

    python training/train.py                 # 12-GB-Karte (z. B. RTX 3080 Ti): Grundmodell in 4 Bit (QLoRA)
    python training/train.py --modus 24gb    # ab 24 GB (gemietete Karte): Grundmodell in bf16, laut Unsloth besser

Ergebnis: training/out/max-lora/ (der Adapter im Hugging-Face-Format). Danach: python training/export.py

Vorher einmal: python training/build_dataset.py  (baut training/data/max.jsonl)
"""
import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
BASE_MODEL = "unsloth/Qwen3.5-9B"

parser = argparse.ArgumentParser(description="Max-Adapter trainieren")
parser.add_argument("--modus", choices=["12gb", "24gb"], default="12gb",
                    help="12gb: Grundmodell in 4 Bit (passt auf 12 GB). 24gb: in bf16 (braucht ~22 GB, bessere Qualität).")
parser.add_argument("--epochen", type=float, default=3, help="Wie oft der Datensatz durchlaufen wird (Standard 3).")
parser.add_argument("--rang", type=int, default=16, help="LoRA-Rang (Standard 16). Größer = mehr Kapazität, mehr Speicher.")
parser.add_argument("--laenge", type=int, default=2048, help="Höchstlänge eines Beispiels in Tokens (Standard 2048).")
parser.add_argument("--probe", action="store_true", help="Nur 5 Schritte – zum Ausprobieren, ob alles läuft.")
args = parser.parse_args()

data = ROOT / "data" / "max.jsonl"
if not data.exists():
    sys.exit("Erst den Datensatz bauen: python training/build_dataset.py")

# Unsloth zuerst importieren – es passt transformers und trl beim Import an.
from unsloth import FastLanguageModel  # noqa: E402
from unsloth.chat_templates import train_on_responses_only  # noqa: E402
import torch  # noqa: E402
from datasets import Dataset  # noqa: E402
from trl import SFTConfig, SFTTrainer  # noqa: E402

if not torch.cuda.is_available():
    sys.exit("Keine Grafikkarte mit CUDA gefunden. Unter Windows: in WSL starten (siehe training/README.md).")
gpu = torch.cuda.get_device_properties(0)
vram = gpu.total_memory / 1024**3
print(f"Grafikkarte: {gpu.name}, {vram:.0f} GB – Modus {args.modus}")
if args.modus == "24gb" and vram < 22:
    sys.exit(f"Modus 24gb braucht etwa 22 GB, die Karte hat {vram:.0f} GB. Nimm --modus 12gb.")
if args.modus == "12gb":
    print("Hinweis: Unsloth rät bei Qwen3.5 eigentlich von 4-Bit-Training ab (etwas ungenauer). "
          "Ob der Adapter trotzdem gut ist, zeigt der Vergleich in Max – sonst später mit --modus 24gb.")

model, processor = FastLanguageModel.from_pretrained(
    BASE_MODEL,
    max_seq_length=args.laenge,
    load_in_4bit=args.modus == "12gb",
)
tokenizer = getattr(processor, "tokenizer", processor)

model = FastLanguageModel.get_peft_model(
    model,
    r=args.rang,
    lora_alpha=args.rang * 2,
    lora_dropout=0,
    target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
    bias="none",
    use_gradient_checkpointing="unsloth",   # spart viel Speicher
    random_state=3407,
)

rows = [json.loads(line) for line in data.read_text(encoding="utf-8").splitlines() if line.strip()]
lengths = [len(tokenizer(r["text"], add_special_tokens=False)["input_ids"]) for r in rows]
too_long = [r["name"] for r, n in zip(rows, lengths) if n > args.laenge]
print(f"{len(rows)} Beispiele, {sum(lengths):,} Tokens, längstes {max(lengths)}".replace(",", "."))
if too_long:
    print(f"Achtung: {len(too_long)} Beispiel(e) länger als {args.laenge} Tokens, werden abgeschnitten: {', '.join(too_long[:5])}")
dataset = Dataset.from_list([{"text": r["text"]} for r in rows])

trainer = SFTTrainer(
    model=model,
    tokenizer=tokenizer,
    train_dataset=dataset,
    args=SFTConfig(
        dataset_text_field="text",
        per_device_train_batch_size=1,
        gradient_accumulation_steps=4,       # wirkt wie 4 Beispiele auf einmal
        warmup_steps=5,
        num_train_epochs=args.epochen,
        max_steps=5 if args.probe else -1,
        learning_rate=2e-4,
        logging_steps=5,
        optim="adamw_8bit",
        weight_decay=0.001,
        lr_scheduler_type="linear",
        seed=3407,
        output_dir=str(ROOT / "out" / "checkpoints"),
        save_strategy="no",
        report_to="none",
    ),
)
# Gelernt wird nur, was Max schreibt – nicht die Fragen und nicht die Werkzeug-Ergebnisse (die stehen als "user").
trainer = train_on_responses_only(trainer, instruction_part="<|im_start|>user\n", response_part="<|im_start|>assistant\n")

stats = trainer.train()
print(f"Fertig in {stats.metrics['train_runtime'] / 60:.0f} Minuten, Verlust am Ende {stats.metrics['train_loss']:.3f}")

out = ROOT / "out" / "max-lora"
model.save_pretrained(str(out))
tokenizer.save_pretrained(str(out))
print(f"Adapter gespeichert: {out}")

# Kurze Probe: drei Fragen, wie Max sie ohne Nachdenken beantworten würde.
FastLanguageModel.for_inference(model)
system = (ROOT / "system-kurz.md").read_text(encoding="utf-8")
for key, value in {"{{datum}}": "Montag, 5. Oktober 2026", "{{uhrzeit}}": "10:00", "{{os}}": "Windows 11",
                   "{{name}}": "Alex", "{{vorname}}": "Alex", "{{gedaechtnis}}": ""}.items():
    system = system.replace(key, value)
for question in ["Wer bist du?", "Welches Sprachmodell steckt in dir?", "Was ist 987654321 mal 123456789?"]:
    prompt = (f"<|im_start|>system\n{system.strip()}\n<|im_end|>\n<|im_start|>user\n{question}<|im_end|>\n"
              "<|im_start|>assistant\n<think>\n\n</think>\n\n")
    inputs = tokenizer(prompt, return_tensors="pt", add_special_tokens=False).to("cuda")
    output = model.generate(**inputs, max_new_tokens=200, temperature=0.7, top_p=0.8, top_k=20)
    answer = tokenizer.decode(output[0][inputs["input_ids"].shape[1]:], skip_special_tokens=True)
    print(f"\n› {question}\n◆ {answer.strip()}")
