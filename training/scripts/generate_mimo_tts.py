#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Batch TTS dataset generator via Xiaomi MiMo Speech Synthesis v2.5
API docs: https://mimo.mi.com/docs/zh-CN/quick-start/usage-guide/audio/speech-synthesis-v2.5

Rules from docs:
- Text to synthesize MUST be in role=assistant message
- Optional style instructions in role=user message
- Preset model: mimo-v2.5-tts + audio.voice + audio.format=wav
- base_url: https://api.xiaomimimo.com/v1
- API key: env MIMO_API_KEY (OpenAI-compatible client)

Output: 16 kHz mono PCM WAV for openWakeWord / local KWS training.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import os
import sys
import time
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple

import numpy as np
import soundfile as sf
import yaml
from dotenv import load_dotenv
from openai import OpenAI
from tqdm import tqdm


class RateLimiter:
    """
    Client-side rate limiter to reduce API key throttling risk.
    - min_interval_sec: hard gap between two requests
    - requests_per_minute: sliding 60s window cap (RPM)
    - max_requests_per_hour: optional hourly cap
    On 429 / rate-limit errors, caller should invoke penalize() / wait_retry_after().
    """

    def __init__(
        self,
        min_interval_sec: float = 1.5,
        requests_per_minute: float = 20.0,
        max_requests_per_hour: float = 0.0,
    ):
        self.min_interval_sec = max(0.0, float(min_interval_sec))
        self.requests_per_minute = max(0.0, float(requests_per_minute))
        self.max_requests_per_hour = max(0.0, float(max_requests_per_hour))
        self._last_request_ts = 0.0
        self._minute_stamps: List[float] = []
        self._hour_stamps: List[float] = []
        self._lock_note = ""

    def _prune(self, now: float) -> None:
        cutoff_m = now - 60.0
        cutoff_h = now - 3600.0
        self._minute_stamps = [t for t in self._minute_stamps if t >= cutoff_m]
        self._hour_stamps = [t for t in self._hour_stamps if t >= cutoff_h]

    def wait_turn(self) -> float:
        """Block until a request is allowed. Returns seconds slept."""
        slept = 0.0
        while True:
            now = time.monotonic()
            self._prune(now)
            wait = 0.0

            # 1) min interval
            if self.min_interval_sec > 0 and self._last_request_ts > 0:
                gap = now - self._last_request_ts
                if gap < self.min_interval_sec:
                    wait = max(wait, self.min_interval_sec - gap)

            # 2) RPM sliding window
            if self.requests_per_minute > 0:
                limit = int(self.requests_per_minute)
                if limit <= 0:
                    limit = 1
                if len(self._minute_stamps) >= limit:
                    oldest = self._minute_stamps[0]
                    wait = max(wait, oldest + 60.0 - now + 0.05)

            # 3) hourly cap
            if self.max_requests_per_hour > 0:
                hlim = int(self.max_requests_per_hour)
                if hlim <= 0:
                    hlim = 1
                if len(self._hour_stamps) >= hlim:
                    oldest = self._hour_stamps[0]
                    wait = max(wait, oldest + 3600.0 - now + 0.05)

            if wait <= 0:
                break
            # cap single sleep slice for responsive tqdm
            slice_sleep = min(wait, 2.0)
            time.sleep(slice_sleep)
            slept += slice_sleep
        return slept

    def mark_request(self) -> None:
        now = time.monotonic()
        self._last_request_ts = now
        self._minute_stamps.append(now)
        self._hour_stamps.append(now)
        self._prune(now)

    def penalize(self, extra_sec: float) -> None:
        """After throttling, force extra cool-down before next request."""
        extra = max(0.0, float(extra_sec))
        if extra <= 0:
            return
        # push last stamp into the future via sleep now
        time.sleep(extra)
        self._last_request_ts = time.monotonic()

    @staticmethod
    def is_rate_limit_error(err: BaseException) -> bool:
        msg = str(err).lower()
        markers = (
            "429",
            "rate limit",
            "rate_limit",
            "too many requests",
            "quota",
            "throttl",
            "exceed",
            "限流",
            "频率",
            "过于频繁",
        )
        if any(m in msg for m in markers):
            return True
        status = getattr(err, "status_code", None) or getattr(err, "status", None)
        if status == 429:
            return True
        body = getattr(err, "body", None)
        if body and "429" in str(body):
            return True
        return False

    @staticmethod
    def parse_retry_after_sec(err: BaseException, default: float = 15.0) -> float:
        # try response headers if present (openai-like)
        for attr in ("response", "http_response"):
            resp = getattr(err, attr, None)
            if resp is None:
                continue
            headers = getattr(resp, "headers", None) or {}
            try:
                ra = headers.get("Retry-After") or headers.get("retry-after")
                if ra is not None:
                    return max(1.0, float(ra))
            except Exception:
                pass
        msg = str(err)
        # crude "retry in 12s" patterns
        import re as _re
        m = _re.search(r"retry\s*(?:after|in)\s*(\d+(?:\.\d+)?)\s*s", msg, _re.I)
        if m:
            return max(1.0, float(m.group(1)))
        return max(1.0, float(default))


def normalize_api_key(raw: str | None) -> str:
    if not raw:
        return ""
    key = raw.strip().strip('"').strip("'").strip()
    # allow "Bearer xxx" paste
    if key.lower().startswith("bearer "):
        key = key[7:].strip()
    return key


def mask_api_key(key: str) -> str:
    if not key:
        return "<empty>"
    if len(key) <= 8:
        return "*" * len(key)
    return f"{key[:4]}...{key[-4:]} (len={len(key)})"


def is_auth_error(err: BaseException) -> bool:
    msg = str(err).lower()
    markers = (
        "401",
        "invalid api key",
        "invalid_key",
        "unauthorized",
        "authentication",
        "auth",
        "api key",
        "permission denied",
        "forbidden",
        "403",
    )
    # avoid treating generic "api key missing in body" too loosely: require strong signals
    strong = (
        "401",
        "invalid api key",
        "invalid_key",
        "unauthorized",
        "authenticationerror",
        "403",
        "forbidden",
    )
    if any(m in msg for m in strong):
        return True
    status = getattr(err, "status_code", None) or getattr(err, "status", None)
    return status in (401, 403)


def rel_posix(path: Path, root: Path) -> str:
    return str(path.relative_to(root)).replace(chr(92), '/')


def project_training_root() -> Path:
    # training/scripts/this_file -> training/
    return Path(__file__).resolve().parent.parent


def load_config(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def ensure_dirs(*paths: Path) -> None:
    for p in paths:
        p.mkdir(parents=True, exist_ok=True)


def slugify(text: str, max_len: int = 32) -> str:
    keep = []
    for ch in text.strip():
        if ch.isalnum() or ch in ("-", "_"):
            keep.append(ch)
        elif ch.isspace():
            keep.append("_")
        else:
            # keep CJK
            if "\u4e00" <= ch <= "\u9fff":
                keep.append(ch)
    s = "".join(keep).strip("_")
    return (s[:max_len] if s else "utt")


def short_hash(*parts: str) -> str:
    h = hashlib.sha1("||".join(parts).encode("utf-8")).hexdigest()
    return h[:10]


def balanced_limit(jobs: List["Job"], cap: int, seed: str) -> List["Job"]:
    """Deterministically cap jobs without exhausting the first text first."""
    if cap <= 0 or len(jobs) <= cap:
        return jobs

    jobs_by_text: Dict[str, List[Job]] = {}
    for job in jobs:
        jobs_by_text.setdefault(job.text, []).append(job)

    # The hash gives a stable, well-distributed ordering across voices and styles.
    for text_jobs in jobs_by_text.values():
        text_jobs.sort(
            key=lambda job: short_hash(
                "balanced-limit",
                seed,
                job.split,
                job.text,
                job.voice,
                job.style_prompt,
                job.tag_style,
                job.out_name,
            )
        )

    selected: List[Job] = []
    text_order = list(jobs_by_text)
    while len(selected) < cap:
        made_progress = False
        for text in text_order:
            if jobs_by_text[text] and len(selected) < cap:
                selected.append(jobs_by_text[text].pop(0))
                made_progress = True
        if not made_progress:
            break
    return selected


def decode_api_audio_to_pcm16_mono(
    audio_bytes: bytes,
    target_sr: int = 16000,
) -> Tuple[np.ndarray, int]:
    """Decode API wav/bytes -> float32 mono PCM at target_sr."""
    # Prefer soundfile (wav/flac/ogg). Fallback to wave for plain PCM wav.
    try:
        data, sr = sf.read(io.BytesIO(audio_bytes), always_2d=True)
        # data: [n, ch] float
        mono = data.mean(axis=1).astype(np.float32)
    except Exception:
        with wave.open(io.BytesIO(audio_bytes), "rb") as wf:
            sr = wf.getframerate()
            nch = wf.getnchannels()
            sw = wf.getsampwidth()
            raw = wf.readframes(wf.getnframes())
        if sw == 2:
            arr = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
        else:
            raise RuntimeError(f"unsupported sample width: {sw}")
        if nch > 1:
            arr = arr.reshape(-1, nch).mean(axis=1)
        mono = arr.astype(np.float32)

    if sr != target_sr:
        # linear resample (dependency-free)
        if mono.size == 0:
            return mono, target_sr
        duration = mono.size / float(sr)
        new_len = max(1, int(round(duration * target_sr)))
        x_old = np.linspace(0.0, 1.0, num=mono.size, endpoint=False)
        x_new = np.linspace(0.0, 1.0, num=new_len, endpoint=False)
        mono = np.interp(x_new, x_old, mono).astype(np.float32)
        sr = target_sr

    # peak normalize lightly (avoid clipping)
    peak = float(np.max(np.abs(mono))) if mono.size else 0.0
    if peak > 1e-6:
        mono = (mono / peak * 0.89).astype(np.float32)
    return mono, sr


def write_wav_pcm16(path: Path, mono: np.ndarray, sr: int) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    clipped = np.clip(mono, -1.0, 1.0)
    pcm = (clipped * 32767.0).astype(np.int16)
    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sr)
        wf.writeframes(pcm.tobytes())


@dataclass
class Job:
    split: str  # positive | negative
    text: str
    voice: str
    style_prompt: str
    tag_style: str
    out_name: str

    @property
    def assistant_content(self) -> str:
        if self.tag_style:
            # docs: (风格)待合成内容
            tag = self.tag_style if self.tag_style.startswith("(") else f"({self.tag_style})"
            return f"{tag}{self.text}"
        return self.text


def build_jobs(cfg: dict) -> List[Job]:
    voices: List[str] = list(cfg.get("voices") or ["mimo_default"])
    styles: List[str] = list(cfg.get("style_prompts") or [""])
    tags: List[str] = list(cfg.get("tag_styles") or [""])
    pos_n = int(cfg.get("samples_per_positive_combo") or 1)
    neg_n = int(cfg.get("samples_per_negative_combo") or 1)
    max_pos = int(cfg.get("max_positive") or 10**9)
    max_neg = int(cfg.get("max_negative") or 10**9)

    jobs: List[Job] = []
    sampling_seed = str(cfg.get("sampling_seed") or 0)

    def add(split: str, texts: Iterable[str], repeats: int, cap: int) -> None:
        nonlocal jobs
        split_jobs: List[Job] = []
        for text in texts:
            for voice in voices:
                for style in styles:
                    for tag in tags:
                        for i in range(repeats):
                            key = short_hash(split, text, voice, style, tag, str(i))
                            name = f"{split}_{slugify(text)}_{slugify(voice, 12)}_{key}.wav"
                            split_jobs.append(
                                Job(
                                    split=split,
                                    text=text,
                                    voice=voice,
                                    style_prompt=style or "用自然清晰的普通话朗读。",
                                    tag_style=tag or "",
                                    out_name=name,
                                )
                            )
        jobs.extend(balanced_limit(split_jobs, cap, sampling_seed))

    add("positive", cfg.get("positive_texts") or [], pos_n, max_pos)
    add("negative", cfg.get("negative_texts") or [], neg_n, max_neg)
    return jobs


def synthesize_one(
    client: OpenAI,
    model: str,
    job: Job,
    audio_format: str = "wav",
    timeout: float = 120.0,
) -> bytes:
    # OpenAI python SDK: chat.completions.create supports extra body fields via kwargs on some versions.
    # MiMo expects audio={format, voice} like the official example.
    completion = client.chat.completions.create(
        model=model,
        messages=[
            {"role": "user", "content": job.style_prompt},
            {"role": "assistant", "content": job.assistant_content},
        ],
        audio={
            "format": audio_format,
            "voice": job.voice,
        },
        timeout=timeout,
    )
    message = completion.choices[0].message
    audio_obj = getattr(message, "audio", None)
    if audio_obj is None:
        # some SDKs return dict-like
        dump = message.model_dump() if hasattr(message, "model_dump") else {}
        audio_obj = dump.get("audio")
    if audio_obj is None:
        raise RuntimeError("API response missing message.audio")

    data_b64 = audio_obj.get("data") if isinstance(audio_obj, dict) else getattr(audio_obj, "data", None)
    if not data_b64:
        raise RuntimeError("API response missing audio.data (base64)")
    return base64.b64decode(data_b64)


def run(args: argparse.Namespace) -> int:
    root = project_training_root()
    load_dotenv(root / ".env")
    load_dotenv(root.parent / ".env")  # optional repo root

    api_key = normalize_api_key(
        os.environ.get("MIMO_API_KEY") or os.environ.get("XIAOMI_MIMO_API_KEY")
    )

    base_url = (os.environ.get("MIMO_BASE_URL") or "https://api.xiaomimimo.com/v1").strip().rstrip("/")
    cfg_path = Path(args.config)
    if not cfg_path.is_absolute():
        cfg_path = (root / cfg_path).resolve()
    cfg = load_config(cfg_path)

    model = os.environ.get("MIMO_TTS_MODEL") or cfg.get("model") or "mimo-v2.5-tts"
    target_sr = int(cfg.get("target_sample_rate") or 16000)
    audio_format = cfg.get("audio_format") or "wav"
    # Rate limit (prefer new keys; fall back to sleep_between_requests_sec)
    min_interval = float(
        cfg.get("min_interval_sec")
        if cfg.get("min_interval_sec") is not None
        else cfg.get("sleep_between_requests_sec")
        if cfg.get("sleep_between_requests_sec") is not None
        else 1.5
    )
    rpm = float(cfg.get("requests_per_minute") if cfg.get("requests_per_minute") is not None else 20)
    rph = float(cfg.get("max_requests_per_hour") if cfg.get("max_requests_per_hour") is not None else 0)
    # CLI overrides
    if getattr(args, "min_interval", None) is not None:
        min_interval = float(args.min_interval)
    if getattr(args, "rpm", None) is not None:
        rpm = float(args.rpm)
    if getattr(args, "rph", None) is not None:
        rph = float(args.rph)

    retries = int(cfg.get("max_retries") or 6)
    timeout = float(cfg.get("request_timeout_sec") or 120)
    backoff_base = float(cfg.get("rate_limit_backoff_base_sec") or 8.0)
    backoff_max = float(cfg.get("rate_limit_backoff_max_sec") or 120.0)
    stop_on_rate_limit = bool(cfg.get("stop_on_repeated_rate_limit") if cfg.get("stop_on_repeated_rate_limit") is not None else True)
    max_consecutive_rate_limits = int(cfg.get("max_consecutive_rate_limits") or 5)

    limiter = RateLimiter(
        min_interval_sec=min_interval,
        requests_per_minute=rpm,
        max_requests_per_hour=rph,
    )

    pos_dir = root / (cfg.get("output_positive_dir") or "data/positive")
    neg_dir = root / (cfg.get("output_negative_dir") or "data/negative")
    manifest_path = root / (cfg.get("manifest_path") or "data/meta/manifest.jsonl")
    ensure_dirs(pos_dir, neg_dir, manifest_path.parent)

    jobs = build_jobs(cfg)
    if args.limit and args.limit > 0:
        jobs = jobs[: args.limit]

    if args.dry_run:
        print(f"config={cfg_path}")
        print(f"model={model} base_url={base_url}")
        print(f"rate_limit: min_interval={min_interval}s rpm={rpm} rph={rph or 'off'}")
        print(f"jobs={len(jobs)} positive={sum(j.split=='positive' for j in jobs)} negative={sum(j.split=='negative' for j in jobs)}")
        # rough ETA assuming avg 1 req uses max(min_interval, 60/rpm)
        per = max(min_interval, (60.0 / rpm) if rpm > 0 else min_interval)
        print(f"ETA_approx_sec={int(len(jobs) * per)} (~{per:.2f}s/req lower-bound)")
        for j in jobs[:12]:
            print(f"  - [{j.split}] voice={j.voice} text={j.text!r} tag={j.tag_style!r}")
        if len(jobs) > 12:
            print(f"  ... {len(jobs)-12} more")
        return 0

    if not api_key:
        print("ERROR: set MIMO_API_KEY in training/.env (copy from .env.example)", file=sys.stderr)
        return 2
    placeholders = {
        "your_api_key_here",
        "changeme",
        "xxx",
        "todo",
        "paste_here",
        "sk-xxxx",
    }
    if api_key.lower() in placeholders or api_key.lower().startswith("your_"):
        print(
            "ERROR: MIMO_API_KEY still looks like a placeholder. "
            "Put the real key from https://mimo.mi.com/ into training/.env",
            file=sys.stderr,
        )
        return 2

    # Official curl uses header "api-key"; OpenAI SDK uses Bearer.
    # Send both to maximize compatibility.
    client = OpenAI(
        api_key=api_key,
        base_url=base_url,
        default_headers={
            "api-key": api_key,
            "Authorization": f"Bearer {api_key}",
        },
    )
    print(f"auth: key={mask_api_key(api_key)} base_url={base_url}")
    print(
        f"rate_limit enabled: min_interval={min_interval}s rpm={rpm} "
        f"rph={rph or 'off'} retries={retries}"
    )

    # append manifest
    done = 0
    failed = 0
    rate_limit_hits = 0
    consecutive_rate_limits = 0
    with manifest_path.open("a", encoding="utf-8") as mf:
        pbar = tqdm(jobs, desc="MiMo TTS")
        for job in pbar:
            out_dir = pos_dir if job.split == "positive" else neg_dir
            out_path = out_dir / job.out_name
            if out_path.exists() and not args.overwrite:
                done += 1
                continue

            last_err: Optional[Exception] = None
            audio_bytes: Optional[bytes] = None
            for attempt in range(1, retries + 1):
                slept = limiter.wait_turn()
                if slept > 0.2:
                    pbar.set_postfix_str(f"throttle_wait={slept:.1f}s")
                try:
                    limiter.mark_request()
                    audio_bytes = synthesize_one(
                        client, model=model, job=job, audio_format=audio_format, timeout=timeout
                    )
                    last_err = None
                    consecutive_rate_limits = 0
                    break
                except Exception as e:
                    last_err = e
                    if is_auth_error(e):
                        tqdm.write(
                            "AUTH_ERROR: " + str(e) + "\n"
                            "Check training/.env MIMO_API_KEY (loaded " + mask_api_key(api_key) + ").\n"
                            "Open https://mimo.mi.com/ -> create/copy key into training/.env as:\n"
                            "  MIMO_API_KEY=your_real_key\n"
                            "No quotes/spaces. If you set MIMO_API_KEY in system env, update/restart shell."
                        )
                        mf.write(
                            json.dumps(
                                {
                                    "ok": False,
                                    "error": "auth:" + str(e),
                                    "split": job.split,
                                    "text": job.text,
                                    "voice": job.voice,
                                    "path": rel_posix(out_path, root),
                                },
                                ensure_ascii=False,
                            )
                            + "\n"
                        )
                        mf.flush()
                        print(f"done={done} failed={failed + 1} aborted_auth manifest={manifest_path}")
                        return 4
                    if RateLimiter.is_rate_limit_error(e):
                        rate_limit_hits += 1
                        consecutive_rate_limits += 1
                        ra = RateLimiter.parse_retry_after_sec(
                            e, default=min(backoff_max, backoff_base * (2 ** (attempt - 1)))
                        )
                        ra = min(backoff_max, max(ra, backoff_base))
                        tqdm.write(
                            f"RATE_LIMIT attempt={attempt}/{retries} "
                            f"backoff={ra:.1f}s file={job.out_name} err={e}"
                        )
                        limiter.penalize(ra)
                        if stop_on_rate_limit and consecutive_rate_limits >= max_consecutive_rate_limits:
                            tqdm.write(
                                f"STOP: {consecutive_rate_limits} consecutive rate-limits. "
                                f"Lower rpm/min_interval or retry later. "
                                f"done={done} failed={failed}"
                            )
                            mf.write(
                                json.dumps(
                                    {
                                        "ok": False,
                                        "error": f"aborted_rate_limit:{e}",
                                        "split": job.split,
                                        "text": job.text,
                                        "voice": job.voice,
                                        "path": rel_posix(out_path, root),
                                    },
                                    ensure_ascii=False,
                                )
                                + "\n"
                            )
                            mf.flush()
                            print(
                                f"done={done} failed={failed + 1} rate_limit_hits={rate_limit_hits} "
                                f"manifest={manifest_path}"
                            )
                            return 3
                    else:
                        consecutive_rate_limits = 0
                        # normal transient errors
                        time.sleep(min(backoff_base, 2.0 * attempt))

            if last_err is not None or audio_bytes is None:
                failed += 1
                rec = {
                    "ok": False,
                    "error": str(last_err),
                    "split": job.split,
                    "text": job.text,
                    "voice": job.voice,
                    "style_prompt": job.style_prompt,
                    "tag_style": job.tag_style,
                    "path": rel_posix(out_path, root),
                }
                mf.write(json.dumps(rec, ensure_ascii=False) + "\n")
                mf.flush()
                tqdm.write(f"FAIL {job.out_name}: {last_err}")
                continue

            try:
                mono, sr = decode_api_audio_to_pcm16_mono(audio_bytes, target_sr=target_sr)
                write_wav_pcm16(out_path, mono, sr)
                rec = {
                    "ok": True,
                    "split": job.split,
                    "label": 1 if job.split == "positive" else 0,
                    "text": job.text,
                    "voice": job.voice,
                    "style_prompt": job.style_prompt,
                    "tag_style": job.tag_style,
                    "assistant_content": job.assistant_content,
                    "sample_rate": sr,
                    "duration_sec": float(mono.size / float(sr)) if sr else 0.0,
                    "path": rel_posix(out_path, root),
                    "model": model,
                }
                mf.write(json.dumps(rec, ensure_ascii=False) + "\n")
                mf.flush()
                done += 1
            except Exception as e:
                failed += 1
                tqdm.write(f"WRITE FAIL {job.out_name}: {e}")
                mf.write(
                    json.dumps(
                        {
                            "ok": False,
                            "error": f"write:{e}",
                            "split": job.split,
                            "text": job.text,
                            "voice": job.voice,
                            "path": rel_posix(out_path, root),
                        },
                        ensure_ascii=False,
                    )
                    + "\n"
                )
                mf.flush()


    print(f"done={done} failed={failed} rate_limit_hits={rate_limit_hits} manifest={manifest_path}")
    print("Next: use data/positive + data/negative for openWakeWord / custom trainer.")
    return 0 if failed == 0 else 1


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate wake-word TTS dataset via MiMo v2.5 TTS")
    parser.add_argument(
        "--config",
        default="config/wakeword_nihao_luolisi.yaml",
        help="YAML config path relative to training/ or absolute",
    )
    parser.add_argument("--limit", type=int, default=0, help="only first N jobs (debug)")
    parser.add_argument("--dry-run", action="store_true", help="print plan only")
    parser.add_argument("--overwrite", action="store_true", help="regenerate existing wavs")
    parser.add_argument("--rpm", type=float, default=None, help="max requests per minute (override config)")
    parser.add_argument("--rph", type=float, default=None, help="max requests per hour (override config, 0=off)")
    parser.add_argument("--min-interval", type=float, default=None, dest="min_interval", help="min seconds between requests (override config)")
    args = parser.parse_args()
    raise SystemExit(run(args))


if __name__ == "__main__":
    main()
