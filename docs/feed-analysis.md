# PNR feed sample review and structure

> **Superseded design.** This review describes the per-feed parsers, field policies and the three XML files (`feed-policies.xml`, `xdr-layouts.xml`, `reference-layouts.xml`) that decoded-text detection replaced. Those files and parsers are removed. Protection now uses the generic decoder and the PNR recognizer file, which keeps passenger names only (`passengerName` and `memberName`); Presidio's predefined recognizers run too only with `Presidio:DefaultDetection` on, which is off by default. The other fields described below are published as received; see [Decoded-text detection](../README.md#decoded-text-detection). The sample review, the structure findings and the field meanings below stay valid as analysis; the configuration notes refer to the removed design. The protection results they produced are frozen, value-free, in [tests/fixtures/parity-baseline.json](../tests/fixtures/parity-baseline.json).
>
> This branch (`PNR-Worker`) keeps the PNR feed only. The review of the other feeds, and the configuration examples of the removed design (which used one of those feeds), are in the repository history at commit `dffbdad`.

Both files in `docs/sample-docs/PNR/` were reviewed without changing source bytes. Each is an MQ text export: `A` rows carry metadata and `X` rows carry hexadecimal payload bytes. Both are binary payloads, and neither is a negative test.

## Binary representation

The examples use big-endian 32-bit words and counted ASCII strings padded to four bytes. A counted string consists of its byte length, that many text bytes and zero padding. An empty string is a length word of zero, with no value or padding. Missing text is therefore represented structurally, not by a special character. Actual `*` characters in populated fields are masked/placeholder content and are not universal missing-field markers.

The initial PNR word is two. The removed schema checked this observed discriminator. Subsequent words and string counts are parsed separately, and offsets are calculated while reading each message.

A scanner can read some pairs of raw words as a short counted string: for example `00000001 31000000` looks like a length-one string containing `1` with three zero pad bytes. The generic decoder therefore treats only strings of four or more printable characters as text and keeps shorter spans as raw words (WR-015).

## PNR

PNR has two examples, PNR.txt (5,452 payload bytes) and PNR_02.txt (1,780). The XDR schema follows the sections of the P2 PNR Record Layout (keys 01, 10, 20, 30, 40, 43, 50, 60, 80, C0, D0, D2, D8). Repeating sections are rpcgen linked lists: a `1` word precedes each element and a `0` word ends the list. The spec describes the MQ/RTA P2 record and excludes the real-time C-structure feed, so it supplies field meanings, not wire order; element shapes are fitted to both samples, and lists that are empty in both samples rejected any content in the removed design. The reference layout mapped 53 strings, and the PNR policy had 92 fields.

Mixed record texts (SSR/OSI facts, frequent-flyer accounts, remarks and fare references) use ordered variants keyed by record code, such as DOCS, CTCE, CTCM and TKNE. The first matching pattern masks the personal values while the record syntax stays unchanged; a catch-all masks unrecognized records whole. PNR.txt has 80 masked and 74 kept values; PNR_02.txt has 24 and 33.

Some PNR records use `*` between adjacent fields. Shared loyalty, sensitive-attribute and financial-information rules use `#` for masking so separators remain distinguishable when the protected record is parsed again. Other masks use `*`. The output is validated and can be processed a second time without changes.

The two PNR samples do not establish a general producer contract, all collection shapes, unknown word meanings or passenger associations, and neither the spec nor the samples carry a stable passenger ID. In the removed design, unsupported structures went to the dead-letter queue without publication until a reviewed schema represented them; decoded-text detection scans them instead (WR-016).

## Reviewed samples and results

Counts below describe populated binary text/capture fields under the removed field policies. Fixed structural tags, empty text slots and raw framing words are excluded. Retained verification evidence records source hashes and calculated field spans without personal values.

| Sample | Payload bytes | Kept | Masked |
| --- | ---: | ---: | ---: |
| PNR.txt | 5,452 | 74 | 80 Mask |
| PNR_02.txt | 1,780 | 33 | 24 Mask |

With decoded-text detection, both samples are published through the real worker and local Presidio, with `Presidio:DefaultDetection` off. The parity check is names-only: of the 104 values the former field policies protected, the 16 names are in scope, 12 of them protected and 4 approved exceptions (standalone names in which nothing is detected); the other 88 values are out of scope and published as received. Both identified PNR occurrences stay unchanged, and source hashes, payload sizes, raw words, string counts, zero padding, record syntax, MQ headers, row framing and newline style are preserved.

## Configuration behavior (removed design)

The removed design kept three XML files with these responsibilities:

| File | Responsibility |
| --- | --- |
| `reference-layouts.xml` | Readable field names, baseline offsets/lengths and optional capture mappings |
| `xdr-layouts.xml` | Ordered binary words, counted strings, padding, arrays and optional groups |
| `feed-policies.xml` | Per-feed Keep/Mask actions, with an optional shared catalog |

The worker validated them together before it connected to the broker; a restart applied changes. Every exposed scalar needed exactly one policy, unknown structure went to the dead-letter queue without publication, and new structural variations required reviewed XML. Decoded-text detection replaced all three files with `pnr.xml`.

## Reviewed field names

PNR names follow the P2 record layout sections and replace the earlier fixed-offset aliases.

| Original field | Policy alias | Evidence and limits |
| --- | --- | --- |
| `/names/*/surname`, `/firstNames/*/givenName` | `surname`, `givenName` | P2 key 20 `P20PSNM` and key 30 `P20PNAM`; one linked-list item per name, never a passenger identity |
| `/facts/*/text` | `factText` | P2 key 40 `P20PFTX` SSR/OSI text; ordered variants for DOCS, CTCE, CTCM and other record codes |
| `/frequentFlyers/*/account` | `frequentFlyerAccount` | P2 key 43 `P20PFQT`/`P20ESTX` FQTV number and tier text |
| `/remarks/*/text`, `/fareReferences/*/text`, `/etktPayments/*/passengerName` | `remarkText`, `fareReferenceText`, `etktPassengerName` | Remark, fare-reference and electronic-ticket payment records inside the PNR; variants mask personal captures and keep record syntax |

## Reproduce verification

```powershell
dotnet build PresidioDemo.slnx
dotnet run --project tests/FeedPipelineChecks
dotnet run --project tests/WorkerChecks
src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py -v
src/.venv/Scripts/python.exe tests/run_sample_verification.py
```

The sample verification runs both PNR samples through `tests/FeedPipelineHarness`. It keeps the outputs, transaction logs and a value-free `verification.json` in a fresh `.test-runs/e2e-*` folder. The harness exits with code 0 because both samples are published.
