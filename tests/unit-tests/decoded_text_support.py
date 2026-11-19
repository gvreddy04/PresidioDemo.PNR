"""Value-free verification of decoded-text protection against the frozen parity baseline (WR-019).

Shared by test_decoded_text_feeds.py and tests/run_sample_verification.py. Nothing here prints or returns a sample
value; failures name the sample, location and check only. The check covers the baseline values whose entity a recognizer
defined in the feed's recognizer file produces (a custom or lone-name recognizer); every other value is counted as out of
scope, as the spike does.
"""
from functools import lru_cache
from pathlib import Path
import json
import xml.etree.ElementTree as ET

PROJECT = Path(__file__).resolve().parents[2]
FIXTURES = PROJECT / "tests" / "fixtures"
RECOGNIZERS = PROJECT / "src" / "PresidioDemo.Core" / "Configuration" / "recognizers"
# No supplied PNR sample is a negative test; a negative sample added later is listed here with its expected reason.
EXPECTED_REJECTIONS = {}


@lru_cache(maxsize=None)
def defined_entities(feed):
    """Return the entities the enabled custom and lone-name recognizers of a feed's recognizer file produce."""
    root = ET.parse(RECOGNIZERS / (feed.lower() + ".xml")).getroot()
    return frozenset(r.get("supported_entity") for r in root.iter("recognizer")
                     if r.get("type", "custom") in ("custom", "lone_name") and r.get("enabled", "true") == "true")


def in_scope(entry):
    """Return whether the parity check covers a baseline value: a recognizer its feed's file defines produces its entity."""
    return entry["entity"] in defined_entities(entry["feed"])


def payload(message):
    """Return the payload bytes of an IBM MQ export (X rows) or a raw payload."""
    lines = message.decode("utf-8", errors="strict").splitlines() if message[:2] == b"A " else None
    if lines is None:
        return message
    return bytes.fromhex("".join(line[2:].strip() for line in lines if line.startswith("X ")))


def header(message):
    """Return the MQ metadata rows of an export."""
    return [line for line in message.decode("utf-8").splitlines() if line.startswith("A ")]


def baseline():
    """Return the frozen baseline and the approved exceptions as (sample, location) keys."""
    document = json.loads((FIXTURES / "parity-baseline.json").read_text(encoding="utf-8"))
    exceptions = {(e["sample"], e["location"]) for e in json.loads((FIXTURES / "parity-exceptions.json").read_text(encoding="utf-8"))}
    return document, exceptions


def verify_published(feed, name, original, output, document=None, exceptions=None):
    """Check one published sample; return value-free counts, raise AssertionError on a failed check."""
    if document is None:
        document, exceptions = baseline()
    before, after = payload(original), payload(output)
    checks = {"protected": 0, "approved_exceptions": 0, "out_of_scope": 0}
    if original[:2] == b"A ":
        if header(original) != header(output):
            raise AssertionError(f"{name}: MQ metadata changed")
        if original.endswith(b"\n") != output.endswith(b"\n"):
            raise AssertionError(f"{name}: final newline changed")
        rows = [len(line) for line in original.splitlines() if line.startswith(b"X ")]
        # Every protection keeps the width, so the export keeps identical hex rows (WR-004).
        if rows != [len(line) for line in output.splitlines() if line.startswith(b"X ")]:
            raise AssertionError(f"{name}: MQ row framing changed")
    if len(before) != len(after):
        raise AssertionError(f"{name}: binary payload length changed")
    # Every action's result is fitted printable ASCII (WR-015, WR-016), whatever operator the recognizer file chooses.
    for index, (left, right) in enumerate(zip(before, after)):
        if left != right and not 32 <= right < 127:
            raise AssertionError(f"{name}: byte {index} changed to a character that is not printable ASCII")
    for entry in document["protected"]:
        if entry["sample"] != name:
            continue
        if not in_scope(entry):
            # No recognizer the file defines produces this entity: reported with a count, never failed.
            checks["out_of_scope"] += 1
            continue
        start, length = int(entry["location"]), entry["length"]
        caught = all(b == 0x2A or b != a for b, a in zip(before[start:start + length], after[start:start + length]))
        if caught:
            checks["protected"] += 1
        elif (name, entry["location"]) in exceptions:
            checks["approved_exceptions"] += 1
        else:
            raise AssertionError(f"{name}: baseline {entry['entity']} at {entry['location']} was published unprotected")
    return checks
