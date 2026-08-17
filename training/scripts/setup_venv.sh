#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "[1/4] Creating venv at training/.venv ..."
if [[ ! -d .venv ]]; then
  python3 -m venv .venv
fi

echo "[2/4] Upgrade pip ..."
./.venv/bin/python -m pip install -U pip setuptools wheel

echo "[3/4] Install requirements ..."
./.venv/bin/python -m pip install -r requirements.txt

if [[ ! -f .env ]]; then
  cp .env.example .env
  echo "[4/4] Created .env — edit MIMO_API_KEY"
else
  echo "[4/4] .env exists"
fi

echo
echo "OK. Next:"
echo "  source .venv/bin/activate"
echo "  python scripts/generate_mimo_tts.py --dry-run"
