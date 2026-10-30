"""Tests for check_keys.py. Run: python -m unittest discover -s scripts -p "test_*.py"."""
import importlib.util
import os
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SPEC = importlib.util.spec_from_file_location("check_keys", os.path.join(HERE, "check_keys.py"))
ck = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ck)

KEYED = """<?xml version="1.0" encoding="utf-8"?>
<LanguageData>
  <RW_Hello>Hello {0}</RW_Hello>
  <RW_Multi>line one
line two {1}</RW_Multi>
</LanguageData>
"""


class CheckKeysTests(unittest.TestCase):
    def test_parse_keyed_reads_keys_and_values(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "k.xml")
            with open(path, "w", encoding="utf-8") as f:
                f.write(KEYED)
            keyed = ck.parse_keyed(path)
        self.assertEqual(sorted(keyed), ["RW_Hello", "RW_Multi"])
        self.assertEqual(keyed["RW_Hello"], "Hello {0}")
        self.assertIn("{1}", keyed["RW_Multi"])

    def test_key_diffs_reports_a_key_missing_on_either_side(self):
        diffs = ck.key_diffs({"RW_A": "a", "RW_B": "b"}, {"RW_B": "b", "RW_C": "c"})
        self.assertEqual(len(diffs), 2)
        self.assertTrue(any("RW_A" in d and "Japanese" in d for d in diffs))
        self.assertTrue(any("RW_C" in d and "English" in d for d in diffs))

    def test_placeholder_diffs_reports_a_missing_placeholder(self):
        diffs = ck.placeholder_diffs({"RW_X": "{0} and {1}", "RW_Y": "{0}"}, {"RW_X": "{0}", "RW_Y": "{0}"})
        self.assertEqual(len(diffs), 1)
        self.assertIn("RW_X", diffs[0])

    def test_undefined_keys_ignores_def_names_and_trailing_underscore_prefixes(self):
        used = {"RW_Known", "RW_Warehouse", "RW_Kind_", "RW_Unknown"}
        defined = {"RW_Known", "RW_Warehouse", "RW_Kind_Normal"}
        problems = ck.undefined_keys(used, defined)
        self.assertEqual(len(problems), 1)
        self.assertIn("RW_Unknown", problems[0])

    def test_undefined_keys_reports_a_prefix_that_matches_no_key(self):
        problems = ck.undefined_keys({"RW_Rank_"}, {"RW_Known"})
        self.assertEqual(len(problems), 1)
        self.assertIn("RW_Rank_", problems[0])

    def test_the_repository_passes(self):
        self.assertEqual(ck.check(ck.REPO), [])


if __name__ == "__main__":
    unittest.main()
