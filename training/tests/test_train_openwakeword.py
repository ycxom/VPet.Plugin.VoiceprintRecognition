import importlib.util
import sys
import unittest
from pathlib import Path

import numpy as np


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "train_openwakeword.py"
SPEC = importlib.util.spec_from_file_location("train_openwakeword", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class DataPreparationTests(unittest.TestCase):
    def test_clean_view_is_fixed_pcm16_and_right_aligned(self):
        source = np.full(8000, 0.25, dtype=np.float32)
        view = MODULE.prepare_view(source, 32000, 16000, None)

        self.assertEqual(view.shape, (32000,))
        self.assertEqual(view.dtype, np.int16)
        self.assertTrue(np.all(view[:24000] == 0))
        self.assertTrue(np.all(view[24000:] != 0))

    def test_voice_split_has_no_source_leakage(self):
        examples = []
        for voice in ("train-voice", "validation-voice", "test-voice"):
            for label in (0, 1):
                examples.append(MODULE.Example(Path(f"{voice}-{label}.wav"), label, str(label), "text", voice))

        splits = MODULE.split_examples(examples, {"validation-voice"}, {"test-voice"})

        self.assertEqual({item.voice for item in splits["train"]}, {"train-voice"})
        self.assertEqual({item.voice for item in splits["validation"]}, {"validation-voice"})
        self.assertEqual({item.voice for item in splits["test"]}, {"test-voice"})

    def test_threshold_prefers_zero_validation_false_positives(self):
        labels = np.array([0, 0, 0, 1, 1, 1], dtype=np.float32)
        scores = np.array([0.1, 0.2, 0.3, 0.2, 0.4, 0.9], dtype=np.float32)

        threshold = MODULE.select_threshold(labels, scores)
        metrics = MODULE.binary_metrics(labels, scores, threshold)

        self.assertAlmostEqual(threshold, 0.35, places=6)
        self.assertEqual(metrics["fp"], 0)
        self.assertEqual(metrics["tp"], 2)


if __name__ == "__main__":
    unittest.main()
