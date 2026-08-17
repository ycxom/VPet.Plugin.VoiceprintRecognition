# Setup Python venv for MiMo TTS dataset generation (Windows)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "[1/4] Creating venv at training/.venv ..."
if (-not (Test-Path ".venv")) {
  python -m venv .venv
}

Write-Host "[2/4] Activate & upgrade pip ..."
& .\.venv\Scripts\python.exe -m pip install -U pip setuptools wheel

Write-Host "[3/4] Install requirements ..."
& .\.venv\Scripts\python.exe -m pip install -r requirements.txt

if (-not (Test-Path ".env")) {
  Copy-Item ".env.example" ".env"
  Write-Host "[4/4] Created .env — please edit MIMO_API_KEY"
} else {
  Write-Host "[4/4] .env already exists"
}

Write-Host ""
Write-Host "OK. Next:"
Write-Host "  1) Edit training\.env  set MIMO_API_KEY=..."
Write-Host "  2) .\.venv\Scripts\Activate.ps1"
Write-Host "  3) python scripts\generate_mimo_tts.py --dry-run"
Write-Host "  4) python scripts\generate_mimo_tts.py --limit 8"
Write-Host "  5) python scripts\generate_mimo_tts.py"
