#!/usr/bin/env python3
"""Check MIMO_API_KEY loading without printing the secret."""
from pathlib import Path
import os
from dotenv import load_dotenv

root = Path(__file__).resolve().parent.parent
load_dotenv(root / ".env")
load_dotenv(root.parent / ".env")

raw = os.environ.get("MIMO_API_KEY") or os.environ.get("XIAOMI_MIMO_API_KEY") or ""
key = raw.strip().strip('"').strip("'")
if key.lower().startswith("bearer "):
    key = key[7:].strip()

print("training/.env exists:", (root / ".env").exists())
print("key loaded:", bool(key))
print("key length:", len(key))
if not key:
    print("STATUS: MISSING")
elif key.lower() in {"your_api_key_here", "paste_your_real_key_here", "changeme", "xxx"} or key.lower().startswith("your_"):
    print("STATUS: PLACEHOLDER - replace with real key from https://mimo.mi.com/")
elif len(key) < 16:
    print("STATUS: SUSPICIOUSLY_SHORT")
else:
    print("STATUS: PRESENT")
    print("preview:", key[:4] + "..." + key[-4:])
print("base_url:", os.environ.get("MIMO_BASE_URL", "https://api.xiaomimimo.com/v1"))
