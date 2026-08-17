import importlib.util
import sys
import unittest
from collections import Counter
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "generate_mimo_tts.py"
SPEC = importlib.util.spec_from_file_location("generate_mimo_tts", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class BuildJobsTests(unittest.TestCase):
    def test_caps_are_balanced_across_texts_and_deterministic(self):
        config = {
            "positive_texts": ["target-a", "target-b", "target-c"],
            "negative_texts": ["negative-a", "negative-b", "negative-c"],
            "voices": ["voice-a", "voice-b"],
            "style_prompts": ["style-a", "style-b"],
            "tag_styles": ["", "calm"],
            "samples_per_positive_combo": 1,
            "samples_per_negative_combo": 1,
            "max_positive": 7,
            "max_negative": 7,
            "sampling_seed": 42,
        }

        first = MODULE.build_jobs(config)
        second = MODULE.build_jobs(config)

        self.assertEqual([job.out_name for job in first], [job.out_name for job in second])
        for split in ("positive", "negative"):
            split_jobs = [job for job in first if job.split == split]
            counts = Counter(job.text for job in split_jobs)
            self.assertEqual(len(split_jobs), 7)
            self.assertLessEqual(max(counts.values()) - min(counts.values()), 1)
            self.assertEqual({job.voice for job in split_jobs}, {"voice-a", "voice-b"})


if __name__ == "__main__":
    unittest.main()
