# PNR feed sample review and structure

> **Superseded design.** This review describes the per-feed parsers, field policies and the three XML files (`feed-policies.xml`, `xdr-layouts.xml`, `reference-layouts.xml`) that decoded-text detection replaced. Those files and parsers are removed. Protection now uses the generic decoder and the PNR recognizer file, which masks the `Mask` fields of the removed policy listed below, each by a recognizer; Presidio's predefined recognizers run too only with `Presidio:DefaultDetection` on, which is off by default; see [Decoded-text detection](../README.md#decoded-text-detection). The sample review, the structure findings and the field meanings below stay valid as analysis; the configuration notes refer to the removed design. The protection results they produced are frozen, value-free, in [tests/fixtures/parity-baseline.json](../tests/fixtures/parity-baseline.json).
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

With decoded-text detection, both samples are published through the real worker and local Presidio, with `Presidio:DefaultDetection` off. All 104 values the former field policies protected are in scope: 57 are protected and 47 are approved exceptions (35 free-text remarks, 7 strings of one to three characters, 2 phone remarks whose passenger marker is kept, 1 phone number in a string of its own, 1 change identifier without a letter A-F and 1 received-from code). Both identified PNR occurrences stay unchanged, and source hashes, payload sizes, raw words, string counts, zero padding, record syntax, MQ headers, row framing and newline style are preserved.

## PNR field list and its recognizers

Every `Mask` field of the removed PNR field policy, with the recognizers in `pnr.xml` that cover it and the approved-exception categories in [tests/fixtures/parity-exceptions.json](../tests/fixtures/parity-exceptions.json) for what they leave. `tests/unit-tests/test_pnr_field_coverage.py` keeps the same table and checks it against both files. `Keep` fields (dates and times, the record locator `/pnr`, segments, `phoneCity`, `MileageText`, `MileageQuoteText`, `FareText` and the like) are not listed and stay unchanged.

| Field | Recognizers | Exception categories |
| --- | --- | --- |
| `/p2CreationTime` | `creationTime` | - |
| `/originChangeIdentifier` | `changeIdentifier` | - |
| `/originIdentifier` | `originIdentifier` | - |
| `/surname` | `passengerName`, `loneNamePart` | - |
| `/givenName` | `passengerName`, `loneNamePart` | - |
| `/factText/IssuingCountry` | `docsIssuingCountry` | - |
| `/factText/DocumentNumber` | `docsDocumentNumber` | - |
| `/factText/Nationality` | `docsNationality`, `osiNationality` | - |
| `/factText/DateOfBirth` | `dateOfBirth` | - |
| `/factText/Gender` | `gender` | - |
| `/factText/DocumentExpiry` | `docsExpiry` | - |
| `/factText/PassengerName` | `passengerName` | - |
| `/factText/AssociatedName` | `passengerName` | - |
| `/factText/EmailAddress` | `ctceEmail` | - |
| `/factText/PhoneNumber` | `ctcmPhone` | - |
| `/factText/TicketNumber` | `ticketNumber` | - |
| `/factText/Text` | `osiNationality` | free text |
| `/frequentFlyerAccount/Account` | `loyaltyNumber` | - |
| `/frequentFlyerAccount/Status` | `loyaltyStatus` | - |
| `/frequentFlyerAccount/Text` | `loyaltyNumber` | - |
| `/frequentFlyerHolderName` | `passengerName` | - |
| `/phoneAreaCode` | `phoneRemark` | short string |
| `/phoneText/PhoneRemark` | - | phone marker |
| `/phoneText/Text` | `phoneRemark` | bare phone |
| `/remarkText/CardSuffix` | `cardSuffix` | - |
| `/remarkText/IpAddress` | `ipAddress` | - |
| `/remarkText/MemberName` | `memberName` | - |
| `/remarkText/MemberAccount` | `loyaltyNumber` | - |
| `/remarkText/MemberStatus` | `loyaltyStatus` | - |
| `/remarkText/SponsorAccount` | `loyaltyNumber` | - |
| `/remarkText/Balance` | `accountBalance` | - |
| `/remarkText/MsoIdentifier` | `msoId` | - |
| `/remarkText/RequestedEmail` | `requestedEmail` | - |
| `/remarkText/FareQuoteReference` | `orderId` | - |
| `/remarkText/TicketOrderReference` | `orderId` | - |
| `/remarkText/AddressCity` | `address` | - |
| `/remarkText/AddressState` | `address` | - |
| `/remarkText/AddressPostalCode` | `address` | - |
| `/remarkText/AddressText` | `address` | - |
| `/remarkText/RviReference` | `rviId` | - |
| `/remarkText/ApprovalReference` | `approvalCode` | - |
| `/remarkText/MileageOrderReference` | `orderId` | - |
| `/remarkText/Text` | - | free text |
| `/fareReferenceText/LoyaltyAccount` | `loyaltyNumber` | - |
| `/fareReferenceText/TicketReference` | `ticketNumber` | - |
| `/fareReferenceText/Text` | - | free text |
| `/receivedFromCode` | `changeIdentifier` | received-from code |
| `/receivedFromText` | `originIdentifier` | free text |
| `/remarkIndexText/LoyaltyAccount` | `loyaltyNumber` | - |
| `/remarkIndexText/Text` | - | free text |
| `/etktPassengerName` | `passengerName` | - |
| `/historyChangeIdentifier` | `changeIdentifier` | zero identifier |
| `/passengerHistoryText` | - | free text |

The categories: **free text**, a remark with no catch-all, published unchanged apart from what the recognizers find in it; **short string**, a binary string of one to three characters (WR-015); **phone marker**, the passenger marker after a phone remark's digits, kept as decided; **bare phone**, a phone number in a string of its own without that marker; **zero identifier**, a change identifier without a letter A-F, which the rule needs so that a six-digit creation time stays unchanged; **received-from code**, a code outside the hexadecimal identifier shape.

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
