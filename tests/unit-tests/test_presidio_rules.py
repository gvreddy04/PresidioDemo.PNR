"""Unit checks for the local detection bridge: spans only, per-feed registries from the Presidio-style file, the
Presidio:DefaultDetection switch (defaultDetection in the bridge JSON), timeouts. Every value is made up."""
from pathlib import Path
import json
import subprocess
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "PresidioDemo.Core" / "Protection" / "python"))
import presidio_demo


SPIKE = Path(__file__).resolve().parents[2] / "tests" / "DetectionParityChecks" / "bin" / "Debug" / "net10.0" / "DetectionParityChecks.dll"


def config(feed, disabled=(), recognizers=(), default_detection=False):
    """Return the bridge JSON for a feed: the default-detection switch, switched-off predefined recognizers and custom
    recognizers."""
    return json.dumps({"feed": feed, "language": "en", "defaultDetection": default_detection, "disabled": list(disabled),
                       "recognizers": list(recognizers)})


def custom(name, entity, *patterns, deny_list=(), score=0.9):
    """Return one custom recognizer for the bridge JSON."""
    return {"name": name, "entity": entity, "patterns": [{"name": name, "regex": p, "score": score} for p in patterns],
            "context": [], "denyList": list(deny_list), "denyListScore": 1.0}


def spans(feed, *texts):
    """Return (entity, matched text, recognizer) per input string from the configured feed."""
    result = json.loads(presidio_demo.analyze_strings(feed, json.dumps(list(texts))))
    return [[(entity, text[start:end], name) for start, end, entity, score, name in found] for text, found in zip(texts, result)]


class DetectionBridgeTests(unittest.TestCase):
    """The batch detection bridge (T-006): spans only, per-feed registries, no protection decisions."""

    @classmethod
    def setUpClass(cls):
        run = subprocess.run(["dotnet", str(SPIKE), "bridge-json"], capture_output=True, text=True, timeout=300)
        if run.returncode != 0:
            raise AssertionError("Build tests/DetectionParityChecks first: " + run.stdout + run.stderr)
        cls.lines = [line for line in run.stdout.splitlines() if line.strip()]
        cls.feeds = [presidio_demo.configure_feed(line) for line in cls.lines]
        # The shipped file with the switch on, under its own feed name, for the default-detection-on cases.
        shipped = json.loads(cls.lines[0])
        cls.custom_names = {r["name"] for r in shipped["recognizers"]}
        presidio_demo.configure_feed(json.dumps(dict(shipped, feed="ShippedOn", defaultDetection=True)))
        presidio_demo.configure_feed(config("OnlyA", recognizers=[custom("demoCode", "DEMO_CODE", r"DEMO-\d{4}")]))
        presidio_demo.configure_feed(config("OnlyB", default_detection=True))
        presidio_demo.configure_feed(config("Nothing"))
        presidio_demo.configure_feed(config("NoCards", disabled=["CreditCardRecognizer"], default_detection=True))
        presidio_demo.configure_feed(config("Titles", recognizers=[custom("titles", "TITLE", deny_list=["MR", "DR"])]))

    def test_the_pnr_feed_configures(self):
        self.assertEqual(sorted(self.feeds), ["PNR"])

    def test_the_shipped_file_is_bridged_with_default_detection_off(self):
        self.assertTrue(self.custom_names)
        self.assertTrue(all(json.loads(line)["defaultDetection"] is False for line in self.lines))

    def test_docs_record_returns_only_the_passenger_names(self):
        found = spans("PNR", "DOCSDAHK1/////12MAR85/F//MILLER/JORDAN-1MILLER/JORDAN")[0]
        self.assertEqual([(e, t) for e, t, n in found], [("PERSON", "MILLER/JORDAN"), ("PERSON", "MILLER/JORDAN")])

    def test_default_detection_off_with_recognizers_runs_only_the_defined_ones(self):
        mixed, guess = spans("PNR", "CTCEDAHK1 alex@example.com 1MILLER/JORDAN", "Contact Alex Sample in Paris")
        self.assertEqual(mixed, [("PERSON", "MILLER/JORDAN", "passengerName")],
                         "the made-up email stays undetected; the SURNAME/GIVEN name is found")
        self.assertEqual(guess, [], "no spaCy PERSON or LOCATION guess runs")

    def test_default_detection_on_with_recognizers_runs_both(self):
        found = spans("ShippedOn", "CTCEDAHK1 alex@example.com 1MILLER/JORDAN")[0]
        self.assertIn(("EMAIL_ADDRESS", "alex@example.com", "EmailRecognizer"), found)
        self.assertIn(("PERSON", "MILLER/JORDAN", "passengerName"), found)

    def test_built_in_email_is_found_in_free_text_with_default_detection_on(self):
        found = spans("ShippedOn", "Contact emily.carter@example.com today")[0]
        self.assertIn("EMAIL_ADDRESS", [entity for entity, text, name in found])

    def test_default_detection_on_without_recognizers_runs_only_presidio(self):
        found = spans("OnlyB", "Card 4111 1111 1111 1111", "Contact Alex Sample", "MILLER/JORDAN")
        self.assertIn(("CREDIT_CARD", "4111 1111 1111 1111", "CreditCardRecognizer"), found[0])
        self.assertIn(("PERSON", "Alex Sample", "SpacyRecognizer"), found[1])
        self.assertFalse(self.custom_names & {n for each in found for e, t, n in each}, "no custom recognizer acts")

    def test_default_detection_off_without_recognizers_detects_nothing(self):
        self.assertEqual(spans("Nothing", "Card 4111 1111 1111 1111", "Contact Alex Sample", "alex@example.com", "MILLER/JORDAN"),
                         [[], [], [], []])

    def test_default_detection_must_be_true_or_false(self):
        for value in ("yes", None, 1):
            with self.subTest(value=value), self.assertRaises(ValueError):
                presidio_demo.configure_feed(json.dumps(dict(json.loads(config("Bad")), defaultDetection=value)))
        missing = json.loads(config("Missing"))
        del missing["defaultDetection"]
        with self.assertRaises(KeyError):
            presidio_demo.configure_feed(json.dumps(missing))

    def test_a_custom_recognizer_applies_only_to_its_own_feed(self):
        self.assertIn(("DEMO_CODE", "DEMO-1234", "demoCode"), spans("OnlyA", "ref DEMO-1234")[0])
        self.assertNotIn("demoCode", [n for e, t, n in spans("OnlyB", "ref DEMO-1234")[0]])
        self.assertNotIn("passengerName", [n for e, t, n in spans("OnlyB", "MILLER/JORDAN")[0]])

    def test_a_switched_off_predefined_recognizer_detects_nothing(self):
        self.assertNotIn("CREDIT_CARD", [e for e, t, n in spans("NoCards", "Card 4111 1111 1111 1111")[0]])

    def test_an_unknown_predefined_recognizer_name_is_rejected_in_both_modes(self):
        for default_detection in (False, True):
            with self.subTest(default_detection=default_detection), self.assertRaises(ValueError):
                presidio_demo.configure_feed(config("Typo", disabled=["CreditCardRecogniser"], default_detection=default_detection))

    def test_a_deny_list_recognizer_detects_its_words(self):
        self.assertIn(("TITLE", "DR", "titles"), spans("Titles", "SEE DR LI")[0])

    def test_a_pattern_timeout_raises_instead_of_skipping_the_pattern(self):
        # Presidio would skip a timed-out pattern and return no span; the bridge raises so .NET dead-letters (WR-016).
        presidio_demo.configure_feed(config("Slow", recognizers=[custom("slow", "DEMO_CODE", r"(A|AA)+$")]))
        with self.assertRaises(TimeoutError):
            presidio_demo.analyze_strings("Slow", json.dumps(["A" * 40 + "!"]))
        self.assertIn("passengerName", [n for e, t, n in spans("PNR", "MILLER/JORDAN")[0]], "the next call works normally")

    def test_offsets_are_utf16_so_dotnet_masks_the_right_characters(self):
        text = "\U0001F600 CONTACT MILLER/JORDAN"
        found = json.loads(presidio_demo.analyze_strings("PNR", json.dumps([text])))[0]
        name = [span for span in found if span[4] == "passengerName"][0]
        utf16 = text.encode("utf-16-le")
        # The name pattern allows spaces, so the match starts at CONTACT, right after the emoji (two UTF-16 units) and a space.
        self.assertEqual(name[0], 3)
        self.assertEqual(utf16[name[0] * 2:name[1] * 2].decode("utf-16-le"), "CONTACT MILLER/JORDAN")

    def test_batch_keeps_one_result_per_string_and_rejects_bad_input(self):
        self.assertEqual(len(spans("PNR", "20261005", "MILLER/JORDAN", "")), 3)
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("PNR", json.dumps([1, 2]))
        with self.assertRaises(ValueError):
            presidio_demo.analyze_strings("NoSuchFeed", "[]")


if __name__ == "__main__":
    unittest.main()
