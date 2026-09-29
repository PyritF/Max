#!/usr/bin/env python3
"""
Wandelt den trainierten Adapter (training/out/max-lora/) in das Format um, das Max lädt (GGUF):

    python training/export.py

Ergebnis: training/out/max-adapter.gguf (etwa 50–150 MB) plus Größe und SHA-256 für das Manifest.
Dafür wird einmal llama.cpp nach training/llama.cpp geholt – daraus nur das Umwandlungsskript.
"""
import hashlib
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
LORA = ROOT / "out" / "max-lora"
OUT = ROOT / "out" / "max-adapter.gguf"
LLAMA = ROOT / "llama.cpp"
BASE_MODEL = "unsloth/Qwen3.5-9B"

if not (LORA / "adapter_config.json").exists():
    sys.exit("Kein Adapter gefunden – erst trainieren: python training/train.py")

if not LLAMA.exists():
    print("Hole llama.cpp (nur für das Umwandlungsskript) …")
    subprocess.run(["git", "clone", "--depth", "1", "https://github.com/ggml-org/llama.cpp", str(LLAMA)], check=True)
    subprocess.run([sys.executable, "-m", "pip", "install", "--quiet", "gguf", "sentencepiece", "protobuf"], check=True)

subprocess.run([
    sys.executable, str(LLAMA / "convert_lora_to_gguf.py"), str(LORA),
    "--base-model-id", BASE_MODEL,
    "--outtype", "f16",
    "--outfile", str(OUT),
], check=True)

digest = hashlib.sha256(OUT.read_bytes()).hexdigest()
print(f"\nFertig: {OUT}")
print(f"  Größe:   {OUT.stat().st_size:,} Bytes".replace(",", "."))
print(f"  SHA-256: {digest}")
print("\nZum Ausprobieren in Max die Datei als adapter.bin in den Datenordner kopieren (siehe README.md).")
