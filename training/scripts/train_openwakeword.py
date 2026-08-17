#!/usr/bin/env python3
"""Train and validate an openWakeWord-compatible ONNX classification head.

This intentionally reuses the exact melspectrogram and embedding ONNX models
shipped with the plugin. Only the small wake-word head is trained.
"""

from __future__ import annotations

import argparse
import hashlib
import inspect
import json
import math
import os
import platform
import random
import shutil
import sys
import time
import wave
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Sequence

import numpy as np
import yaml


TRAINING_ROOT = Path(__file__).resolve().parent.parent


@dataclass(frozen=True)
class Example:
    path: Path
    label: int
    split_name: str
    text: str
    voice: str


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def stable_seed(seed: int, *parts: str) -> int:
    payload = "||".join([str(seed), *parts]).encode("utf-8")
    return int.from_bytes(hashlib.sha256(payload).digest()[:8], "little")


def resolve_from_training_root(value: str | Path) -> Path:
    path = Path(value)
    return path if path.is_absolute() else (TRAINING_ROOT / path).resolve()


def load_examples(manifest_path: Path) -> list[Example]:
    manifest_root = manifest_path.parents[2]
    examples: dict[Path, Example] = {}
    for line_number, line in enumerate(manifest_path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
        except json.JSONDecodeError as exc:
            raise ValueError(f"Invalid JSON at {manifest_path}:{line_number}: {exc}") from exc
        if row.get("ok") is not True or "label" not in row or not row.get("path"):
            continue
        path = (manifest_root / row["path"]).resolve()
        if not path.is_file():
            raise FileNotFoundError(f"Manifest audio is missing: {path}")
        examples[path] = Example(
            path=path,
            label=int(row["label"]),
            split_name=str(row.get("split") or ("positive" if int(row["label"]) else "negative")),
            text=str(row.get("text") or ""),
            voice=str(row.get("voice") or "unknown"),
        )
    result = sorted(examples.values(), key=lambda item: str(item.path))
    if not result or {item.label for item in result} != {0, 1}:
        raise ValueError("The manifest must contain successful examples for both labels")
    return result


def split_examples(
    examples: Sequence[Example],
    validation_voices: set[str],
    test_voices: set[str],
) -> dict[str, list[Example]]:
    overlap = validation_voices & test_voices
    if overlap:
        raise ValueError(f"Validation and test voices overlap: {sorted(overlap)}")
    known_voices = {item.voice for item in examples}
    missing = (validation_voices | test_voices) - known_voices
    if missing:
        raise ValueError(f"Configured held-out voices do not exist: {sorted(missing)}")

    result = {"train": [], "validation": [], "test": []}
    for item in examples:
        if item.voice in validation_voices:
            result["validation"].append(item)
        elif item.voice in test_voices:
            result["test"].append(item)
        else:
            result["train"].append(item)

    for split_name, split_items in result.items():
        counts = Counter(item.label for item in split_items)
        if counts[0] == 0 or counts[1] == 0:
            raise ValueError(f"{split_name} must contain both labels; got {dict(counts)}")
    return result


def read_pcm16_mono(path: Path, target_sample_rate: int) -> np.ndarray:
    with wave.open(str(path), "rb") as wav:
        channels = wav.getnchannels()
        sample_width = wav.getsampwidth()
        sample_rate = wav.getframerate()
        raw = wav.readframes(wav.getnframes())
    if sample_width != 2:
        raise ValueError(f"Only PCM16 WAV is supported: {path} has {sample_width * 8}-bit samples")
    pcm = np.frombuffer(raw, dtype="<i2").astype(np.float32)
    if channels > 1:
        pcm = pcm.reshape(-1, channels).mean(axis=1)
    pcm /= 32768.0
    if sample_rate != target_sample_rate:
        new_length = max(1, int(round(len(pcm) * target_sample_rate / sample_rate)))
        pcm = np.interp(
            np.linspace(0.0, 1.0, new_length, endpoint=False),
            np.linspace(0.0, 1.0, len(pcm), endpoint=False),
            pcm,
        ).astype(np.float32)
    return np.clip(pcm, -1.0, 1.0)


def trim_silence(samples: np.ndarray, sample_rate: int) -> np.ndarray:
    frame_samples = max(1, int(round(sample_rate * 0.02)))
    rms = np.array(
        [
            math.sqrt(float(np.mean(samples[start : start + frame_samples] ** 2)) + 1e-12)
            for start in range(0, len(samples), frame_samples)
        ],
        dtype=np.float32,
    )
    threshold = max(0.003, min(0.03, float(rms.max(initial=0.0)) * 0.08))
    active = np.flatnonzero(rms >= threshold)
    if not active.size:
        return np.empty(0, dtype=np.float32)
    padding = int(round(sample_rate * 0.10))
    start = max(0, int(active[0]) * frame_samples - padding)
    end = min(len(samples), (int(active[-1]) + 1) * frame_samples + padding)
    return samples[start:end].copy()


def resample_by_speed(samples: np.ndarray, speed: float) -> np.ndarray:
    new_length = max(1, int(round(len(samples) / speed)))
    return np.interp(
        np.linspace(0.0, 1.0, new_length, endpoint=False),
        np.linspace(0.0, 1.0, len(samples), endpoint=False),
        samples,
    ).astype(np.float32)


def prepare_view(
    active_samples: np.ndarray,
    clip_samples: int,
    sample_rate: int,
    rng: np.random.Generator | None,
) -> np.ndarray:
    samples = active_samples.copy()
    trailing_silence = 0
    if rng is not None:
        samples = resample_by_speed(samples, float(rng.uniform(0.90, 1.10)))
        gain_db = float(rng.uniform(-12.0, 3.0))
        samples *= 10.0 ** (gain_db / 20.0)
        trailing_silence = int(rng.integers(0, max(1, int(sample_rate * 0.20)) + 1))

        if rng.random() < 0.85:
            signal_rms = math.sqrt(float(np.mean(samples**2)) + 1e-12)
            snr_db = float(rng.uniform(8.0, 30.0))
            noise_rms = max(1e-5, signal_rms / (10.0 ** (snr_db / 20.0)))
            samples += rng.normal(0.0, noise_rms, len(samples)).astype(np.float32)

        if rng.random() < 0.35:
            frequency = float(rng.choice([50.0, 60.0, 100.0, 120.0]))
            phase = float(rng.uniform(0.0, 2.0 * math.pi))
            amplitude = float(rng.uniform(0.0005, 0.006))
            t = np.arange(len(samples), dtype=np.float32) / sample_rate
            samples += amplitude * np.sin(2.0 * math.pi * frequency * t + phase)

    samples = np.clip(samples, -1.0, 1.0)
    if len(samples) > clip_samples:
        samples = samples[-clip_samples:]
        trailing_silence = 0

    output = np.zeros(clip_samples, dtype=np.float32)
    end = max(len(samples), clip_samples - trailing_silence)
    start = max(0, end - len(samples))
    output[start:end] = samples[-(end - start) :]
    return np.round(np.clip(output, -1.0, 1.0) * 32767.0).astype(np.int16)


class FeatureExtractor:
    def __init__(
        self,
        ort: Any,
        melspectrogram_path: Path,
        embedding_path: Path,
        n_feature_frames: int,
        embedding_dim: int,
    ) -> None:
        options = ort.SessionOptions()
        options.inter_op_num_threads = 1
        options.intra_op_num_threads = max(1, min(8, os.cpu_count() or 1))
        self.mel_session = ort.InferenceSession(
            str(melspectrogram_path), options, providers=["CPUExecutionProvider"]
        )
        self.embedding_session = ort.InferenceSession(
            str(embedding_path), options, providers=["CPUExecutionProvider"]
        )
        self.mel_input = self.mel_session.get_inputs()[0].name
        self.embedding_input = self.embedding_session.get_inputs()[0].name
        self.n_feature_frames = n_feature_frames
        self.embedding_dim = embedding_dim

    def extract(self, pcm16: np.ndarray) -> np.ndarray:
        mel_output = self.mel_session.run(
            None, {self.mel_input: pcm16.astype(np.float32, copy=False)[None, :]}
        )[0]
        mel = np.squeeze(mel_output)
        if mel.ndim != 2:
            raise ValueError(f"Unexpected melspectrogram output shape: {mel_output.shape}")
        if mel.shape[-1] != 32 and mel.shape[0] == 32:
            mel = mel.T
        if mel.shape[-1] != 32:
            raise ValueError(f"Expected 32 mel bands, got {mel.shape}")
        mel = mel.astype(np.float32) / 10.0 + 2.0

        windows = [mel[start : start + 76] for start in range(0, mel.shape[0], 8)]
        windows = [window for window in windows if window.shape == (76, 32)]
        if len(windows) < self.n_feature_frames:
            raise ValueError(
                f"Expected at least {self.n_feature_frames} embedding windows, got {len(windows)} "
                f"from mel shape {mel.shape}"
            )
        windows_array = np.asarray(windows[-self.n_feature_frames :], dtype=np.float32)[..., None]
        embedding_output = self.embedding_session.run(
            None, {self.embedding_input: windows_array}
        )[0]
        embeddings = np.asarray(embedding_output, dtype=np.float32).reshape(-1, self.embedding_dim)
        embeddings = embeddings[-self.n_feature_frames :]
        expected = (self.n_feature_frames, self.embedding_dim)
        if embeddings.shape != expected:
            raise ValueError(f"Expected embeddings {expected}, got {embeddings.shape}")
        return embeddings


def prepare_source_audio(
    examples: Sequence[Example], sample_rate: int, max_active_seconds: float
) -> tuple[dict[Path, np.ndarray], list[dict[str, Any]]]:
    prepared: dict[Path, np.ndarray] = {}
    skipped: list[dict[str, Any]] = []
    for item in examples:
        samples = trim_silence(read_pcm16_mono(item.path, sample_rate), sample_rate)
        duration = len(samples) / sample_rate
        if samples.size == 0 or duration > max_active_seconds:
            skipped.append(
                {
                    "path": str(item.path.relative_to(TRAINING_ROOT)),
                    "reason": "silent" if samples.size == 0 else "active_audio_too_long",
                    "active_duration_seconds": round(duration, 6),
                }
            )
            continue
        prepared[item.path] = samples
    return prepared, skipped


def extract_split_features(
    split_name: str,
    examples: Sequence[Example],
    prepared_audio: dict[Path, np.ndarray],
    extractor: FeatureExtractor,
    clip_samples: int,
    sample_rate: int,
    seed: int,
    views_per_clip: int,
) -> tuple[np.ndarray, np.ndarray, list[str]]:
    features: list[np.ndarray] = []
    labels: list[int] = []
    sources: list[str] = []
    total = sum(views_per_clip if item.path in prepared_audio else 0 for item in examples)
    done = 0
    started = time.monotonic()
    for item in examples:
        active = prepared_audio.get(item.path)
        if active is None:
            continue
        relative = str(item.path.relative_to(TRAINING_ROOT)).replace("\\", "/")
        for view_index in range(views_per_clip):
            rng = None
            if split_name == "train" and view_index > 0:
                rng = np.random.default_rng(stable_seed(seed, relative, str(view_index)))
            pcm = prepare_view(active, clip_samples, sample_rate, rng)
            features.append(extractor.extract(pcm))
            labels.append(item.label)
            sources.append(relative)
            done += 1
            if done % 100 == 0 or done == total:
                elapsed = max(0.001, time.monotonic() - started)
                print(f"features {split_name}: {done}/{total} ({done / elapsed:.1f} clips/s)", flush=True)
    return (
        np.asarray(features, dtype=np.float32),
        np.asarray(labels, dtype=np.float32),
        sources,
    )


def make_synthetic_negative(index: int, clip_samples: int, sample_rate: int, seed: int) -> np.ndarray:
    rng = np.random.default_rng(stable_seed(seed, "synthetic-negative", str(index)))
    t = np.arange(clip_samples, dtype=np.float32) / sample_rate
    kind = index % 6
    if kind == 0:
        samples = rng.normal(0.0, float(rng.uniform(0.001, 0.08)), clip_samples)
    elif kind == 1:
        samples = rng.normal(0.0, float(rng.uniform(0.0002, 0.008)), clip_samples)
        for frequency in rng.choice(np.arange(80, 1200, 20), size=3, replace=False):
            samples += float(rng.uniform(0.001, 0.02)) * np.sin(
                2.0 * math.pi * float(frequency) * t + float(rng.uniform(0, 2 * math.pi))
            )
    elif kind == 2:
        samples = rng.normal(0.0, 0.0005, clip_samples)
        for _ in range(int(rng.integers(1, 12))):
            location = int(rng.integers(0, clip_samples))
            width = int(rng.integers(1, 80))
            samples[location : location + width] += float(rng.uniform(-0.3, 0.3))
    elif kind == 3:
        samples = rng.normal(0.0, 0.0001, clip_samples)
    elif kind == 4:
        # Decaying tone sequences approximate notifications and ringtones.
        samples = rng.normal(0.0, 0.0002, clip_samples)
        cursor = int(rng.integers(0, sample_rate // 2))
        for _ in range(int(rng.integers(2, 7))):
            length = int(rng.integers(sample_rate // 20, sample_rate // 3))
            end = min(clip_samples, cursor + length)
            local_t = np.arange(end - cursor, dtype=np.float32) / sample_rate
            envelope = np.exp(-local_t * float(rng.uniform(4.0, 15.0)))
            frequency = float(rng.uniform(250.0, 2200.0))
            samples[cursor:end] += float(rng.uniform(0.01, 0.15)) * envelope * np.sin(
                2.0 * math.pi * frequency * local_t
            )
            cursor = min(clip_samples, end + int(rng.integers(0, sample_rate // 5)))
            if cursor >= clip_samples:
                break
    else:
        # Chirps cover alarms and electronic sweeps without using system assets.
        samples = rng.normal(0.0, 0.0002, clip_samples)
        start_frequency = float(rng.uniform(150.0, 1200.0))
        end_frequency = float(rng.uniform(800.0, 3500.0))
        phase = 2.0 * math.pi * (
            start_frequency * t
            + (end_frequency - start_frequency) * t**2 / (2.0 * max(float(t[-1]), 1e-6))
        )
        samples += float(rng.uniform(0.002, 0.05)) * np.sin(phase)
    return np.round(np.clip(samples, -1.0, 1.0) * 32767.0).astype(np.int16)


def roc_auc(labels: np.ndarray, scores: np.ndarray) -> float:
    positive = scores[labels == 1]
    negative = scores[labels == 0]
    if not len(positive) or not len(negative):
        return float("nan")
    comparisons = positive[:, None] - negative[None, :]
    return float((np.sum(comparisons > 0) + 0.5 * np.sum(comparisons == 0)) / comparisons.size)


def average_precision(labels: np.ndarray, scores: np.ndarray) -> float:
    order = np.argsort(-scores)
    sorted_labels = labels[order]
    positives = int(np.sum(sorted_labels == 1))
    if positives == 0:
        return float("nan")
    true_positives = np.cumsum(sorted_labels == 1)
    precision = true_positives / (np.arange(len(sorted_labels)) + 1)
    return float(np.sum(precision[sorted_labels == 1]) / positives)


def binary_metrics(labels: np.ndarray, scores: np.ndarray, threshold: float) -> dict[str, Any]:
    predicted = scores >= threshold
    actual = labels == 1
    tp = int(np.sum(predicted & actual))
    tn = int(np.sum(~predicted & ~actual))
    fp = int(np.sum(predicted & ~actual))
    fn = int(np.sum(~predicted & actual))
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    specificity = tn / (tn + fp) if tn + fp else 0.0
    return {
        "threshold": round(float(threshold), 6),
        "count": int(len(labels)),
        "tp": tp,
        "tn": tn,
        "fp": fp,
        "fn": fn,
        "accuracy": (tp + tn) / len(labels),
        "balanced_accuracy": (recall + specificity) / 2.0,
        "precision": precision,
        "recall": recall,
        "false_reject_rate": 1.0 - recall,
        "false_positive_rate": fp / (fp + tn) if fp + tn else 0.0,
        "f1": 2 * precision * recall / (precision + recall) if precision + recall else 0.0,
        "roc_auc": roc_auc(labels, scores),
        "average_precision": average_precision(labels, scores),
        "score_min": float(scores.min()),
        "score_median": float(np.median(scores)),
        "score_max": float(scores.max()),
    }


def select_threshold(labels: np.ndarray, scores: np.ndarray) -> float:
    negative_scores = scores[labels == 0]
    positive_scores = scores[labels == 1]
    if not len(negative_scores) or not len(positive_scores):
        return 0.5

    # Wake-word thresholds should strongly prefer no observed false activations.
    # Put the threshold halfway between the highest validation negative and the
    # lowest still-recoverable positive, yielding maximum recall at zero val FP.
    highest_negative = float(np.max(negative_scores))
    recoverable_positives = positive_scores[positive_scores > highest_negative]
    if not len(recoverable_positives):
        return float(np.clip(max(0.5, highest_negative), 0.05, 0.95))
    lowest_recoverable_positive = float(np.min(recoverable_positives))
    return float(np.clip((highest_negative + lowest_recoverable_positive) / 2.0, 0.05, 0.95))


def build_head(torch: Any, mean: np.ndarray, scale: np.ndarray, hidden_size: int) -> Any:
    nn = torch.nn

    class WakeHead(nn.Module):
        def __init__(self) -> None:
            super().__init__()
            self.register_buffer("feature_mean", torch.from_numpy(mean.astype(np.float32)))
            self.register_buffer("feature_scale", torch.from_numpy(scale.astype(np.float32)))
            input_size = int(np.prod(mean.shape[1:]))
            self.flatten = nn.Flatten()
            self.layer1 = nn.Linear(input_size, hidden_size)
            self.norm1 = nn.LayerNorm(hidden_size)
            self.layer2 = nn.Linear(hidden_size, hidden_size)
            self.norm2 = nn.LayerNorm(hidden_size)
            self.output = nn.Linear(hidden_size, 1)

        def forward(self, features: Any) -> Any:
            features = (features - self.feature_mean) / self.feature_scale
            features = torch.relu(self.norm1(self.layer1(self.flatten(features))))
            features = torch.relu(self.norm2(self.layer2(features)))
            return self.output(features)

    return WakeHead()


def predict_scores(torch: Any, model: Any, features: np.ndarray) -> np.ndarray:
    model.eval()
    with torch.no_grad():
        logits = model(torch.from_numpy(features).float()).squeeze(1)
        return torch.sigmoid(logits).cpu().numpy()


def train_head(
    torch: Any,
    train_features: np.ndarray,
    train_labels: np.ndarray,
    validation_features: np.ndarray,
    validation_labels: np.ndarray,
    config: dict[str, Any],
) -> tuple[Any, list[dict[str, float]]]:
    seed = int(config["seed"])
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))

    mean = train_features.mean(axis=0, keepdims=True)
    scale = train_features.std(axis=0, keepdims=True)
    scale = np.maximum(scale, 1e-4)
    model = build_head(torch, mean, scale, int(config["hidden_size"]))
    optimizer = torch.optim.AdamW(
        model.parameters(),
        lr=float(config["learning_rate"]),
        weight_decay=float(config["weight_decay"]),
    )
    positive_count = float(np.sum(train_labels == 1))
    negative_count = float(np.sum(train_labels == 0))
    pos_weight = torch.tensor([negative_count / positive_count], dtype=torch.float32)
    loss_function = torch.nn.BCEWithLogitsLoss(pos_weight=pos_weight)

    x_train = torch.from_numpy(train_features).float()
    y_train = torch.from_numpy(train_labels).float().unsqueeze(1)
    x_validation = torch.from_numpy(validation_features).float()
    y_validation = torch.from_numpy(validation_labels).float().unsqueeze(1)
    generator = torch.Generator().manual_seed(seed)

    best_loss = float("inf")
    best_state: dict[str, Any] | None = None
    epochs_without_improvement = 0
    history: list[dict[str, float]] = []
    batch_size = int(config["batch_size"])
    patience = int(config["early_stopping_patience"])

    for epoch in range(1, int(config["max_epochs"]) + 1):
        model.train()
        permutation = torch.randperm(len(x_train), generator=generator)
        train_loss_sum = 0.0
        for start in range(0, len(permutation), batch_size):
            indices = permutation[start : start + batch_size]
            optimizer.zero_grad(set_to_none=True)
            loss = loss_function(model(x_train[indices]), y_train[indices])
            loss.backward()
            optimizer.step()
            train_loss_sum += float(loss.detach()) * len(indices)

        model.eval()
        with torch.no_grad():
            validation_loss = float(loss_function(model(x_validation), y_validation))
        train_loss = train_loss_sum / len(x_train)
        history.append(
            {"epoch": float(epoch), "train_loss": train_loss, "validation_loss": validation_loss}
        )

        if validation_loss < best_loss - 1e-6:
            best_loss = validation_loss
            best_state = {key: value.detach().cpu().clone() for key, value in model.state_dict().items()}
            epochs_without_improvement = 0
        else:
            epochs_without_improvement += 1

        if epoch == 1 or epoch % 10 == 0:
            print(
                f"epoch {epoch:03d}: train_loss={train_loss:.6f} "
                f"validation_loss={validation_loss:.6f}",
                flush=True,
            )
        if epochs_without_improvement >= patience:
            print(f"early stopping at epoch {epoch}; best validation loss={best_loss:.6f}", flush=True)
            break

    if best_state is None:
        raise RuntimeError("Training did not produce a checkpoint")
    model.load_state_dict(best_state)
    model.eval()
    return model, history


def export_onnx(torch: Any, model: Any, output_path: Path, input_shape: tuple[int, int]) -> None:
    class ProbabilityModel(torch.nn.Module):
        def __init__(self, head: Any) -> None:
            super().__init__()
            self.head = head

        def forward(self, features: Any) -> Any:
            return torch.sigmoid(self.head(features))

    probability_model = ProbabilityModel(model).eval()
    dummy = torch.zeros((1, *input_shape), dtype=torch.float32)
    kwargs: dict[str, Any] = {
        "input_names": ["input"],
        "output_names": ["score"],
        "opset_version": 13,
        "dynamic_axes": {"input": {0: "batch"}, "score": {0: "batch"}},
    }
    if "dynamo" in inspect.signature(torch.onnx.export).parameters:
        kwargs["dynamo"] = False
    output_path.parent.mkdir(parents=True, exist_ok=True)
    torch.onnx.export(probability_model, dummy, str(output_path), **kwargs)


def distribution(items: Iterable[Example]) -> dict[str, Any]:
    items = list(items)
    return {
        "count": len(items),
        "labels": dict(sorted(Counter(item.split_name for item in items).items())),
        "texts": dict(sorted(Counter(item.text for item in items).items())),
        "voices": dict(sorted(Counter(item.voice for item in items).items())),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--config", default=str(TRAINING_ROOT / "config" / "train_nihao_luolisi.yaml")
    )
    parser.add_argument("--no-deploy", action="store_true", help="Do not copy the validated ONNX into the plugin model directory")
    args = parser.parse_args()

    config_path = Path(args.config).resolve()
    config = yaml.safe_load(config_path.read_text(encoding="utf-8"))
    manifest_values = config.get("manifest_paths") or [config.get("manifest_path")]
    if not manifest_values or any(value is None for value in manifest_values):
        raise ValueError("Configure manifest_paths (or legacy manifest_path)")
    manifest_paths = [resolve_from_training_root(value) for value in manifest_values]
    feature_model_dir = resolve_from_training_root(config["feature_model_dir"])
    output_dir = resolve_from_training_root(config["output_dir"])
    deploy_path = resolve_from_training_root(config["deploy_model_path"])
    mel_path = feature_model_dir / "melspectrogram.onnx"
    embedding_path = feature_model_dir / "embedding_model.onnx"
    for required in (*manifest_paths, mel_path, embedding_path):
        if not required.is_file():
            raise FileNotFoundError(required)

    try:
        import onnx
        import onnxruntime as ort
        import torch
    except ImportError as exc:
        print(
            "Training dependencies are missing. Create training/.train-venv with Python 3.11 "
            "and install requirements-train.txt.",
            file=sys.stderr,
        )
        raise SystemExit(2) from exc

    output_dir.mkdir(parents=True, exist_ok=True)
    examples_by_path: dict[Path, Example] = {}
    for manifest_path in manifest_paths:
        for example in load_examples(manifest_path):
            examples_by_path[example.path] = example
    examples = sorted(examples_by_path.values(), key=lambda item: str(item.path))
    splits = split_examples(
        examples,
        set(config.get("validation_voices") or []),
        set(config.get("test_voices") or []),
    )
    prepared_audio, skipped = prepare_source_audio(
        examples,
        int(config["sample_rate"]),
        float(config["max_active_seconds"]),
    )
    print(
        "source split: "
        + ", ".join(f"{name}={len(items)}" for name, items in splits.items())
        + f"; quarantined={len(skipped)}",
        flush=True,
    )

    extractor = FeatureExtractor(
        ort,
        mel_path,
        embedding_path,
        int(config["n_feature_frames"]),
        int(config["embedding_dim"]),
    )
    feature_sets: dict[str, np.ndarray] = {}
    label_sets: dict[str, np.ndarray] = {}
    source_sets: dict[str, list[str]] = {}
    for split_name in ("train", "validation", "test"):
        views = int(config["views_per_training_clip"]) if split_name == "train" else 1
        feature_sets[split_name], label_sets[split_name], source_sets[split_name] = extract_split_features(
            split_name,
            splits[split_name],
            prepared_audio,
            extractor,
            int(config["clip_samples"]),
            int(config["sample_rate"]),
            int(config["seed"]),
            views,
        )

    synthetic_negative_count = int(config.get("synthetic_negative_count") or 0)
    if synthetic_negative_count:
        print(f"features synthetic-negative: 0/{synthetic_negative_count}", flush=True)
        synthetic_features = []
        for index in range(synthetic_negative_count):
            pcm = make_synthetic_negative(
                index,
                int(config["clip_samples"]),
                int(config["sample_rate"]),
                int(config["seed"]),
            )
            synthetic_features.append(extractor.extract(pcm))
        feature_sets["train"] = np.vstack(
            (feature_sets["train"], np.asarray(synthetic_features, dtype=np.float32))
        )
        label_sets["train"] = np.concatenate(
            (label_sets["train"], np.zeros(synthetic_negative_count, dtype=np.float32))
        )
        source_sets["train"].extend(
            [f"synthetic-negative:{index}" for index in range(synthetic_negative_count)]
        )

    feature_cache = output_dir / "features.npz"
    np.savez_compressed(
        feature_cache,
        train_features=feature_sets["train"],
        train_labels=label_sets["train"],
        validation_features=feature_sets["validation"],
        validation_labels=label_sets["validation"],
        test_features=feature_sets["test"],
        test_labels=label_sets["test"],
    )

    model, history = train_head(
        torch,
        feature_sets["train"],
        label_sets["train"],
        feature_sets["validation"],
        label_sets["validation"],
        config,
    )
    checkpoint_path = output_dir / f"{config['model_name']}.pt"
    torch.save(
        {"model_state_dict": model.state_dict(), "config": config, "history": history},
        checkpoint_path,
    )

    scores = {
        split_name: predict_scores(torch, model, feature_sets[split_name])
        for split_name in ("train", "validation", "test")
    }
    selected_threshold = select_threshold(label_sets["validation"], scores["validation"])
    metrics = {
        split_name: {
            "threshold_0_5": binary_metrics(label_sets[split_name], scores[split_name], 0.5),
            "validation_selected_threshold": binary_metrics(
                label_sets[split_name], scores[split_name], selected_threshold
            ),
        }
        for split_name in ("train", "validation", "test")
    }

    onnx_path = output_dir / f"{config['model_name']}.onnx"
    export_onnx(
        torch,
        model,
        onnx_path,
        (int(config["n_feature_frames"]), int(config["embedding_dim"])),
    )
    onnx.checker.check_model(onnx.load(str(onnx_path)))
    onnx_session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    onnx_input = onnx_session.get_inputs()[0]
    onnx_output = onnx_session.get_outputs()[0]
    probe = feature_sets["test"][: min(16, len(feature_sets["test"]))]
    onnx_scores = onnx_session.run(None, {onnx_input.name: probe})[0].reshape(-1)
    parity_error = float(np.max(np.abs(onnx_scores - scores["test"][: len(probe)])))
    expected_shape = ["batch", int(config["n_feature_frames"]), int(config["embedding_dim"])]
    if list(onnx_input.shape) != expected_shape:
        raise ValueError(f"Unexpected ONNX input shape: {onnx_input.shape}; expected {expected_shape}")
    if parity_error > 1e-5:
        raise ValueError(f"PyTorch/ONNX score mismatch: max_abs_error={parity_error}")

    all_file_hashes = {
        str(item.path.relative_to(TRAINING_ROOT)).replace("\\", "/"): sha256_file(item.path)
        for item in examples
    }
    dataset_digest = hashlib.sha256(
        "\n".join(f"{path}:{digest}" for path, digest in sorted(all_file_hashes.items())).encode("utf-8")
    ).hexdigest()
    report = {
        "experimental_baseline": True,
        "created_at_utc": datetime.now(timezone.utc).isoformat(),
        "model_name": config["model_name"],
        "selected_threshold": selected_threshold,
        "threshold_selection_policy": "maximum validation recall with zero observed validation false positives",
        "model": {
            "onnx_path": str(onnx_path),
            "sha256": sha256_file(onnx_path),
            "input": {"name": onnx_input.name, "shape": list(onnx_input.shape), "type": onnx_input.type},
            "output": {"name": onnx_output.name, "shape": list(onnx_output.shape), "type": onnx_output.type},
            "pytorch_onnx_max_abs_error": parity_error,
            "parameter_count": int(sum(parameter.numel() for parameter in model.parameters())),
        },
        "shared_feature_models": {
            "melspectrogram_sha256": sha256_file(mel_path),
            "embedding_sha256": sha256_file(embedding_path),
        },
        "dataset": {
            "manifests": [str(path) for path in manifest_paths],
            "source_count": len(examples),
            "source_sha256": dataset_digest,
            "splits": {name: distribution(items) for name, items in splits.items()},
            "quarantined": skipped,
            "feature_counts": {name: int(len(values)) for name, values in label_sets.items()},
        },
        "metrics": metrics,
        "training": {
            "config": config,
            "epochs_completed": len(history),
            "best_validation_loss": min(item["validation_loss"] for item in history),
        },
        "environment": {
            "python": sys.version,
            "platform": platform.platform(),
            "numpy": np.__version__,
            "torch": torch.__version__,
            "onnx": onnx.__version__,
            "onnxruntime": ort.__version__,
        },
        "limitations": [
            "All positive source clips contain one text: 你好萝莉丝.",
            "All speech negative source clips contain one text: 你好啊.",
            "All source speech is synthetic MiMo TTS; there are no real microphone recordings.",
            "The test set measures held-out TTS voice generalization, not production false positives per hour.",
        ],
    }
    report_path = output_dir / "metrics.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    if not args.no_deploy:
        deploy_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(onnx_path, deploy_path)
        if sha256_file(deploy_path) != report["model"]["sha256"]:
            raise RuntimeError("Deployed model checksum mismatch")

    test_metrics = metrics["test"]["validation_selected_threshold"]
    print(
        f"trained {onnx_path}\n"
        f"threshold={selected_threshold:.4f} test_auc={test_metrics['roc_auc']:.4f} "
        f"test_recall={test_metrics['recall']:.4f} test_fpr={test_metrics['false_positive_rate']:.4f}\n"
        f"onnx_input={onnx_input.shape} output={onnx_output.shape} parity={parity_error:.3g}\n"
        f"report={report_path}",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
