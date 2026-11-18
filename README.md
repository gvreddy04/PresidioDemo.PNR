# Presidio Feed Protection Demo

Process the airline PNR feed from a message broker by decoding each message generically and detecting passenger names with local Presidio and the feed's recognizer file. Presidio's own predefined recognizers run too only when `Presidio:DefaultDetection` is on (off by default). PNR stays unchanged. Output is validated and confirmed by the broker before the input is acknowledged; rejected input goes to the feed's restricted dead-letter queue.

This branch (`PNR-Worker`) runs the PNR feed only.

## Solution organization

The solution separates feed processing from messaging. Broker-specific code lives only in its adapter project, so another broker (for example Kafka or IBM MQ) is added without touching the feed code.

```text
PresidioDemo.slnx
├── src/
│   ├── PresidioDemo.Core/              feed, decoder, detection, pipeline (no broker code)
│   │   ├── Configuration/recognizers/  pnr.xml
│   │   ├── Decoder/                    XdrFeedDecoder + PnrDecoder; XdrTextScanner
│   │   ├── Logging/                    FeedLogger + PnrFeedLogger, FeedTransactionLog, DetectionLog
│   │   ├── Models/                     FeedContracts, FeedFormats, FeedFileRouting
│   │   ├── Pipeline/                   ProtectedFeedWorkflow + PnrWorkflow, FeedWorkflowRouter, codec registry, FeedMessageProcessor
│   │   └── Protection/                 recognizer configuration, detecting protector, python/presidio_demo.py
│   ├── PresidioDemo.Messaging/         broker-neutral ports and models (no broker package)
│   │   ├── Abstractions/               IMessagingConnection, IMessageConsumer, IMessagePublisher
│   │   └── Models/                     InboundMessage, OutboundMessage, MessagingOptions, MessageRoute
│   ├── PresidioDemo.Messaging.Solace/  the only project that references the Solace .NET client
│   │   ├── Connection/                 SolaceOptions, SolaceConnection (session, TLS, reconnect, confirmations)
│   │   ├── Listener/                   SolaceMessageConsumer (queue flow, client acknowledgment)
│   │   ├── Publisher/                  SolaceMessagePublisher (persistent publish, waits for confirmation)
│   │   ├── Models/                     SolaceMessageMapper (native message <-> broker-neutral records)
│   │   └── SolaceMessaging.cs          AddSolaceMessaging registration
│   └── PresidioDemo.Worker/            the deployable: Generic Host, appsettings.json, one FeedWorker for the PNR route
│       ├── Hosting/                    EnvironmentConfiguration, MessagingTransports, PresidioOptions
│       └── Pipeline/                   FeedWorker, FeedWorkerService
├── tests/
│   ├── PresidioDemo.Messaging.Testing/ in-memory broker (tests only, never shipped)
│   ├── Messaging.ContractTests/        the behaviour every broker adapter must pass
│   ├── WorkerChecks/                   environments, settings validation, delivery rules (no Python)
│   ├── FeedPipelineHarness/            runs files through the real worker and Presidio
│   ├── FeedPipelineChecks/ FeedRoundTripChecks/ GenericCodecChecks/   feed and codec checks
│   ├── DetectionParityChecks/          parity gate, recognizer checks, detection-log run, throughput
│   ├── fixtures/                       value-free parity baseline, approved exceptions, spike summary
│   └── unit-tests/                     Python tests, driven through the harness
├── tools/FeedOutputReader/             subscribes to the output topics and logs each message's decoded values
├── tools/FeedSampleSender/             sends sample files to a running worker's input queue
├── Dockerfile                          the worker image (Linux, embedded CPython, Presidio, spaCy model)
└── deploy/
    ├── docker-compose.yml              local Solace broker; with --profile worker, the worker container too
    ├── docker-compose.dev.yml          the worker container against the Dev broker
    └── .env.dev.example                Dev values to copy into the git-ignored deploy/.env.dev
```

Project references point inward only: Worker → Core, Messaging and the adapters; adapters → Messaging. Core never references messaging, and Messaging never references a broker. Concern folders are flat: each holds a base class and the PNR feed's sealed child where the feed needs one, and every namespace is the project plus its folder (for example `PresidioDemo.Core.Decoder`, `PresidioDemo.Messaging.Solace.Connection`, `PresidioDemo.Worker.Hosting`); `test_project_layout.py` checks both. The Python environment stays at `src/.venv`; the container image uses `/opt/venv` through `Presidio:PythonEnvironment`.

![Feed protection workflow](docs/diagrams/presidio-feed-workflow.svg)

## Messaging design

The worker runs one `FeedWorker` for the PNR route. Each message goes through the same steps on every broker:

1. **Receive** from the feed's input queue (`IMessageConsumer`). The broker sends the next message only after this one is acknowledged, so each consumer has one active message.
2. **Process** with `FeedMessageProcessor` (Core): decode, detect and protect with local Presidio, rebuild and validate.
3. **Publish** (`IMessagePublisher`) and wait for the broker's confirmation:
   - valid: the feed's output topic, with a stable transaction ID, the input correlation ID and only the headers listed in `Messaging:CopyHeaders`;
   - invalid: the feed's restricted dead-letter queue, with the original bytes and the reason in the `presidio-reject-reason` header.
4. **Acknowledge** the input (`InboundMessage.CompleteAsync`).

A crash at any step means the broker redelivers the message, so nothing is lost. The transaction ID is derived from the broker's stable message ID, so a redelivered message produces output with the same ID and consumers can drop duplicates (at-least-once delivery). While the broker is unavailable, publishing is retried and the input stays unacknowledged. A message delivered more than `Messaging:MaxDeliveries` times goes to the dead-letter queue without processing. Producers send the feed bytes as the message body; the header `presidio-payload-format` is `raw` (default) or `mq-export` for an IBM MQ export with `A`/`X` lines.

Rejected input and the full before/after comparison contain personal data. In production, rejected input goes only to the dead-letter queue (restrict it with broker access rules), and logs contain only the feed, transaction ID, outcome, field counts and timings. Full before/after logs are written only by the test harness.

### Adding another broker (for example Kafka)

Kafka is not implemented. To add it, or any other broker:

1. Create `src/PresidioDemo.Messaging.Kafka/` with the same folders as the Solace adapter (`Connection/`, `Listener/`, `Publisher/`, `Models/`). Reference only `PresidioDemo.Messaging` and the broker's native client (for Kafka, `Confluent.Kafka`; don't use Schema Registry, which is HTTP).
2. Implement the three ports:

   | Port | Solace (implemented) | Kafka (to implement) |
   | --- | --- | --- |
   | `IMessagingConnection.ConnectAsync` | session over tcp:// or tcps:// | consumer and idempotent producer over SASL/TLS |
   | `IMessageConsumer.ReadAsync` | queue flow, window 1, client acknowledgment | consumer group, `EnableAutoCommit=false`, one message at a time per partition |
   | `InboundMessage.StableId` | replication-group message ID | `topic-partition-offset` |
   | `InboundMessage.DeliveryCount` | broker delivery count | a retry-count header, or 1 |
   | `InboundMessage.CompleteAsync` | `flow.Ack(id)` | `Commit(offset)` in partition order |
   | `IMessagePublisher.PublishAsync` | persistent message, waits for the broker acknowledgment | `ProduceAsync` with `Acks=All` and `EnableIdempotence=true` |
   | Dead-letter destination | Solace queue | dead-letter topic |

3. Add `AddKafkaMessaging(...)` and one case in [MessagingTransports.cs](src/PresidioDemo.Worker/Hosting/MessagingTransports.cs), plus a `Kafka` section in the environments that use it, and set `Messaging:Transport` to `Kafka`.
4. Add the adapter to the list in [tests/Messaging.ContractTests/Program.cs](tests/Messaging.ContractTests/Program.cs). It must pass the same contract as the in-memory broker and Solace.

Nothing in Core, the feed code, the worker or the route changes.

## Configuration and environments

All settings are in one file, [src/PresidioDemo.Worker/appsettings.json](src/PresidioDemo.Worker/appsettings.json):

- **Shared settings** at the top: `Messaging` (transport and delivery limits), `Presidio` (`RecognizerDirectory`, `DefaultDetection`, `DetectionLog`, `DetectionLogValues` and an optional `PythonEnvironment` folder) and `Logging`.
- **`Environments`**: one section per environment, holding only what differs, including its `Messaging:Routes` (`Feed`, `Input`, `Output` and `DeadLetter`). Today these are `Local` (local broker) and `Dev`; each routes the PNR feed only, as route 0. Adding `QA`, `UAT` or `Prod` means adding one more block.
- **`CurrentEnvironment`**: the default when nothing else selects one (`Local`, for developer machines).

The `DOTNET_ENVIRONMENT` variable selects the environment; if it isn't set, `CurrentEnvironment` in the file applies. On AWS, each environment's ECS task definition (or EKS pod spec) sets `DOTNET_ENVIRONMENT` (for example `QA`), so the same image runs everywhere. Values are applied in this order, with later ones winning: shared settings, then the selected environment's section, then environment variables with double underscores (for example `Solace__Host` or `Messaging__Routes__0__Input`). The `Dev` section holds DemoAir stand-in names only; the real broker, VPN, user and queue names are supplied through environment variables at deploy time and are never committed.

Secrets are never in the file. `Solace:PasswordEnvironmentVariable` names the variable holding the password (`SOLACE_PASSWORD`). On AWS, the task definition's `secrets` field injects it from Secrets Manager when the task starts, so the application makes no AWS calls.

Startup stops with one clear message for any of these:
- an environment without a section
- unknown sections or keys
- a password in the file
- an HTTP host, or `tcps://` without certificate validation
- a route for a feed this build does not support, a feed with more than one route, or shared or wildcard destinations. Settings written for a build with more feeds stop startup with, for example, `Messaging:Routes:0 names feed 'TKT', which this build does not support.`
- a `Presidio:PythonEnvironment` folder without `pyvenv.cfg`
- a `Presidio:DefaultDetection` value other than `true` or `false`
- an invalid or missing recognizer file (`Presidio:RecognizerDirectory`)

`Solace:ProvisionQueues` creates the input and dead-letter queues and is enabled only for `Local`; other environments use queues provisioned by the broker team.

**Default detection.** `Presidio:DefaultDetection` (`false` in the base settings, read at startup) decides which recognizers act:

| `Presidio:DefaultDetection` | What acts | Made-up example: `CTCEDAHK1 alex@example.com 1MILLER/JORDAN` |
| --- | --- | --- |
| `false` (default) | Only the recognizers the feed's file defines (`type="custom"`). Presidio's predefined recognizers (email, phone, credit card, IP address, IBAN, spaCy `PERSON`, `NRP`, `LOCATION`, `DATE_TIME` and the rest) are not registered, so they never run. With no custom recognizer, the output equals the input. | `CTCEDAHK1 alex@example.com 1*************` |
| `true` | The defined recognizers plus Presidio's predefined recognizers, minus any the file switches off with `<recognizer type="predefined" enabled="false" />`. Their spans get their entity's `<anonymizer>`, else `DEFAULT`, else Presidio's `replace`. | `CTCEDAHK1 ################ 1*************` |

In both modes the allow list, width fitting, the fail-closed dead letters, the detection log and its review lines work the same. Like the other `Presidio` settings, an environment block or a variable such as `Presidio__DefaultDetection=true` sets it for one feed service. The worker logs the mode once at startup, for example `Default detection off (Presidio:DefaultDetection=false): only the recognizers each feed's file defines act.` With the flag off, spaCy is still loaded and still processes each analyzed string, because the custom recognizers' `<context>` words use its tokens and lemmas; its named entities are not acted on.

**One feed per service.** `Messaging:Feed` (for example `Messaging__Feed=PNR` in a task definition) makes the process consume only that feed's route, so the feed runs as its own ECS service and scales on its own. When it is empty, the process runs every routed feed. A value without a route stops startup.

**Parallel consumers.** `Messaging:ConsumersPerTask` (1 by default, at most 32) opens that many consumers on the routed input queue. All of them share the task's one broker connection. Each consumer reads one message at a time, so broker round trips overlap while Python calls stay serialized. On a partitioned queue, the broker assigns partitions to each consumer, which keeps the order for each partition key. On an exclusive queue, such as the queue the Local broker provisions, the extra consumers wait as hot standby and add no throughput. Set it per task definition, for example `Messaging__ConsumersPerTask=8`, once the feed's queue is partitioned.

**Unique client names.** The broker disconnects an existing connection that reuses a client name. `Solace:UniqueClientName` (on in `Dev`) appends the host name and a random part to `Solace:ClientName`, such as `piidemo-worker-dev-ip-10-0-1-23-ec2-internal-3f9a1c2e`. This lets several tasks of one service connect at the same time, and the name stays within the broker's 160-character limit.

## Architecture diagrams

- [PII infrastructure on AWS](docs/diagrams/aws-pii-infrastructure.svg): proposed dev environment with a Solace broker, an exclusive input queue for the PNR feed and acknowledgment after publish.
- [AWS deployment, one ECS service for the PNR feed](docs/diagrams/aws-solace-ecs-deployment.svg): a generic view of the Harness build and deploy, the Solace broker in AWS, the input queue and DLQ, the PNR worker with parallel consumers and auto scaling, the protected topic with output queues for downstream consumers, and the failed-message and broker backstop paths. Specific values are in [docs/aws-deployment-plan.md](docs/aws-deployment-plan.md).
- [Dead-letter queues](docs/diagrams/pii-feeds-dead-letter-queues.svg): the PNR input queue, its restricted dead-letter queue and who may read it.
- [PNR worker retry flow](docs/diagrams/pnr-worker-retry-flow.svg): redelivery, publish retries and dead-lettering for the PNR worker.
- [Feed protection workflow](docs/diagrams/presidio-feed-workflow.svg): the Solace input queue, the .NET worker (receive, decode, local Presidio, rebuild and validate, publish then acknowledge), the protected topic and the restricted dead-letter queue.

## C# conventions

Types and members include XML summaries. Parameters, return values and property values are documented where applicable, including positional record values and non-public helpers. The application build generates XML documentation.

Declarations use regions for fields and constants, constructors, methods, properties and indexers, and nested types. A file with only one top-level class does not need an outer `Classes` region; member regions inside that class still apply. Methods and properties are ordered by accessibility and then name; overloads stay together. Fields without runtime initializers are sorted by name, while initialized fields retain their construction order. Enum declarations retain their existing numeric and wire order (`FeedType.PNR` keeps the value 1). Control-flow and scoped resource statements use explicit blocks. Documentation does not add runtime annotation attributes.

The root [.editorconfig](.editorconfig) shares C# formatting preferences with Visual Studio. See [C# formatting and cleanup](docs/code-cleanup.md) for the cleanup profile, terminal commands and rules that still require review.

## Decoded-text detection

The PNR feed is decoded generically; there is no schema or field list, so a template change does not stop protection.

`PnrDecoder` (an `XdrFeedDecoder`) reads the IBM MQ export or raw payload, and `XdrTextScanner` scans the big-endian XDR words for counted strings of four or more printable ASCII characters. Shorter spans stay raw words and are never changed, because words such as `00000001 31000000` cannot be told apart from a one-character string (WR-015). A counted string whose text is not printable ASCII but is mostly letters, digits or spaces (Latin-1, EBCDIC, or text with a control character) cannot be checked, so the message is dead-lettered; this includes lowercase and mixed-case EBCDIC. So is a length word followed by printable text that is longer than 65,535 bytes or runs past the end of the payload. Integers, framing and padding are never decoded or changed. Protected values are written back at the same width, and validation rejects any change outside the scanned strings.

The feed's recognizer file is [src/PresidioDemo.Core/Configuration/recognizers/pnr.xml](src/PresidioDemo.Core/Configuration/recognizers/pnr.xml). It is a Presidio-style XML: a flat list under `<recognizers>` that mirrors Presidio's recognizer registry YAML, its `analyze` allow list and its anonymizers map, using Presidio's names (WR-014). Its scope is passenger names only: it defines two `PERSON` recognizers, `passengerName` (`SURNAME/GIVEN`, such as `MILLER/JORDAN MR`) and `memberName` (the name after `MBR` in a loyalty record), with a `PERSON` and a `DEFAULT` anonymizer. Every other PNR value (dates of birth, documents, contacts, tickets, loyalty, payment, identifiers, addresses) is published as received while `Presidio:DefaultDetection` is off. Each decoded string is decided on its own:

1. **Allow list.** A string that matches an `<allow_list>` item end to end (exact text, or a regex with `match="regex"`) is published unchanged without analysis. An item never releases a piece of a longer string: `1405` is kept by `^\d{4}$`, but the name inside `09NOV26 MILLER/JORDAN` is still masked. Regex items match case-insensitively, as patterns do; exact items compare case-sensitively. Each item must be a valid regex on its own: one that only compiles inside the whole-string wrapper (an unbalanced `)`) stops startup.
2. **Detection.** The file's `type="custom"` recognizers run: PNR-specific patterns, context words and deny lists. Only with `Presidio:DefaultDetection` on does every Presidio predefined recognizer run too (spaCy NER for `PERSON`, `LOCATION`, `NRP` and `DATE_TIME`; patterns for `EMAIL_ADDRESS`, `CREDIT_CARD`, `IP_ADDRESS` and the rest), unless the file switches it off by class name with `<recognizer name="…" type="predefined" enabled="false" />`. A misspelled class name stops startup in both modes. Spans scoring below `default_score_threshold` are ignored.
3. **Actions.** Each detected span gets its entity's `<anonymizer>`, else the `DEFAULT` one, else Presidio's default `replace` (`<ENTITY>`). The operators are Presidio's `mask`, `replace`, `redact`, `hash` and `keep`, with Presidio's parameter names (`masking_char`, `chars_to_mask`, `from_end`, `new_value`, `hash_type`, `salt`); `encrypt`, `custom` and `surrogate_ahds` stop startup. Overlapping spans of different entities are resolved by score, then length, then position.
4. **Width.** Every result is fitted to the width of the text it replaces: padded with spaces or cut (WR-015). `Alex Sample` becomes `***********` with `mask`, `<PERSON>` and three spaces with `replace`, eleven spaces with `redact`, and the first eleven hex characters of its hash with `hash`; in a five-character value, `replace` writes `<PERS`.
5. **Nothing detected.** A string in which nothing is detected is published unchanged and listed for review in the detection log. There is no PNR-specific rule: a PNR is published unchanged unless a recognizer detects it.

Local Presidio runs once per message as a batch through Python.NET, and returns spans only; every decision is made in .NET. A message is dead-lettered only for a structural failure: undecodable framing, binary text that is not printable ASCII, a recognizer pattern that times out (the bridge sets Presidio's limit to one second and treats a skipped pattern as a failure), or a rebuild that differs outside the protected strings (WR-016). An invalid recognizer file stops startup with a message naming the file and the problem, such as a retired element (`<builtIn>`, `<keep>`, `<format>`, `<pnrAnchor>`, `defaultMaskCharacter`), an unknown attribute, a rejected operator or a regex that does not compile.

A custom recognizer and actions, with fictional data. With default detection off, `DOCSDAHK1/////12MAR85/F//MILLER/JORDAN-1MILLER/JORDAN` publishes as `DOCSDAHK1/////12MAR85/F//*************-1*************`: the names are masked, the rest of the record is out of scope:

```xml
<recognizer name="passengerName" type="custom" supported_entity="PERSON">
  <pattern name="surname/given" score="0.9">(?&lt;![A-Z])[A-Z]{2,}(?:[ '-][A-Z]+)*/[A-Z]*(?:[ '-][A-Z]+)*</pattern>
</recognizer>
<allow_list match="regex">
  <item>^\d{4}$</item>
</allow_list>
<anonymizer entity="PERSON" type="mask" masking_char="*" />
<anonymizer entity="DEFAULT" type="mask" masking_char="#" />
```

Patterns use syntax shared by .NET and the Python `regex` module and match case-insensitively. `<context>` and `<deny_list>` take space-separated words; a context word near a match raises its score. `PERSON` masks with `*`; `DEFAULT`, which applies only to Presidio's entities when default detection is on, masks with `#` so literal `*` separators stay distinguishable. The shipped allow-list items stop no recognizer the file defines; they are kept because, with default detection on, Presidio's `DATE_TIME` and other guesses would otherwise change dates such as `09NOV26`, times such as `1405`, `FQTR` and fare totals.

### Changing a recognizer or an action

1. Edit `Configuration/recognizers/pnr.xml`: add or change a `<recognizer>`, an `<allow_list>` item or an `<anonymizer>`. Each custom recognizer has a one-line `Example:` comment with fictional values (such as `MILLER/JORDAN`); add one for a new entry, never with real data.
2. Check parity: `src/.venv/Scripts/python.exe tests/run_sample_verification.py`. The check is names-only: every `PERSON` value in [tests/fixtures/parity-baseline.json](tests/fixtures/parity-baseline.json) must stay protected or be listed with a reason in [parity-exceptions.json](tests/fixtures/parity-exceptions.json) (WR-019), and every listed exception must still be needed; a released name fails this developer check, never the worker. Every other baseline value is reported as out of scope (names-only), with a count, and never fails it; nor does masking something the baseline does not list. Both files are value-free: sample, location, length, and an entity or a reason. The baseline was frozen from the field policies an earlier design used and cannot be regenerated. Every `PERSON` baseline value must have a recognizer the file defines (`DetectionParityChecks -- config-checks`); Presidio's own `PERSON` does not count, because it does not run with default detection off. The checks run with the shipped `Presidio:DefaultDetection` off.
3. Open a pull request. The recognizer file ships only inside the image built from it (PR-005), so a change reaches an environment by deploying that image. While decoded-text detection is a POC, that image is deployed to Dev only ([deployment plan](docs/aws-deployment-plan.md)).

### Detection log

`Presidio:DetectionLog` (on in the `Local` environment, off in the base settings and so in `Dev`; for example `Presidio__DetectionLog=true` in the task definition, read at startup) writes one block per message to the worker's standard output (WR-018). The header names the feed, the transaction, the outcome, the source file (`file=`, the message's correlation ID, which the sample sender and the harness set to the file name; `-` when there is none), the number of fields identified (`fields=`), and how many are PII, non-PII and for review. Below it, every decoded string is listed exactly once, in one of three groups:

- **PII fields (changed):** an action changed the string (`decision=changed`).
- **Non-PII fields (unchanged):** an allow-list item kept it (`allowed`), the `keep` operator left it (`kept`), or it is empty.
- **Review (check these; each line has a fix):** the string was left unprotected (`nothing-detected`, or `below-threshold` with its score against the threshold), changed by an entity with no anonymizer of its own (`no-action`), changed by a spaCy guess (`spacy-guess`, only with `Presidio:DefaultDetection` on), or affected by an overlap or a cut result (`overlap`, `cut`). Each review line names its reasons and a fix, such as `fix="add a recognizer if this is personal data, or an allow-list item if it is not"`. With default detection off, every line names a recognizer the file defines, `allow_list` or `none`.

Without the values flag a block holds no value, only lengths and character shapes, so it may be switched on in any environment. A string is located by its slot and its byte offset in the decoded payload (`offsets=payload`); the binary feed has no field names. A made-up example:

```text
PNR 3f9a… DETECTION PUBLISHED file=PNR_DEMO.txt fields=4 pii=1 non-pii=1 review=2 offsets=payload
  PII fields (changed)
    slot 12 @0x01A4  len=13 shape=AAAAAA/AAAAAA  recognizer=passengerName entity=PERSON score=0.90 operator=mask decision=changed
  Non-PII fields (unchanged)
    slot 7 @0x00E0   len=4 shape=9999  recognizer=allow_list entity=- score=0.00 decision=allowed
  Review (check these; each line has a fix)
    slot 2 @0x0018   len=6 shape=AAA999  unchanged  recognizer=none entity=- score=0.00 decision=nothing-detected reason=nothing-detected fix="add a recognizer if this is personal data, or an allow-list item if it is not"
    slot 18 @0x0288  len=5 shape=AAAAA  unchanged  recognizer=none entity=- score=0.00 decision=nothing-detected reason=nothing-detected fix="add a recognizer if this is personal data, or an allow-list item if it is not"
```

An empty group is printed as `(none)`. A message dead-lettered after detection ran has `DEAD-LETTERED` and its reason in the header; a message rejected before detection (for example, malformed framing) has no block. Every line after the header is indented, so a log collector can keep the block as one entry (see the CloudWatch setting in the [deployment plan](docs/aws-deployment-plan.md)).

**Acting on a review entry.** Follow its fix: for personal data that nothing detected, add a custom recognizer for the value's shape; for a value that is not personal data but a spaCy guess changed (default detection on), add an allow-list item; for an entity without its own action, add an `<anonymizer>`. Then follow [Changing a recognizer or an action](#changing-a-recognizer-or-an-action).

**Values in non-production (WR-020).** For testing in Local, Dev and QA, `Presidio:DetectionLogValues` (on by default, read at startup; `Presidio__DetectionLogValues=false` turns it off) adds the actual values: `"before" -> "after"` for a changed string, `"before"` for an unchanged one, and `-> not published` for a dead-lettered message. Values are JSON-encoded, so none of them can break a line:

```text
PNR 3f9a… DETECTION PUBLISHED file=PNR_DEMO.txt fields=4 pii=1 non-pii=1 review=2 offsets=payload
  PII fields (changed)
    slot 12 @0x01A4  "MILLER/JORDAN" -> "*************"  recognizer=passengerName entity=PERSON score=0.90 operator=mask decision=changed
  ...
```

- It needs `Presidio:DetectionLog` on as well. With only the values flag set (as in `Dev` by default), the worker writes no detection blocks and logs one warning at startup.
- It works only when the selected environment is `Local`, `Dev` or `QA` (an unset `DOTNET_ENVIRONMENT` falls back to `Local` and counts). In any other environment, such as `Prod`, the worker still starts: it switches the values flag off, prints one `WARNING` line, and writes value-free blocks only.
- Blocks are written for published messages and for messages dead-lettered after detection ran.
- Outside Local, while both `Presidio:DetectionLog` and `Presidio:DetectionLogValues` are on, the deployment's log group must keep entries for at most 7 days and be readable only by the PII team role. Either flag is switched only through a reviewed task-definition deploy. Because the values flag is on by default, switching the detection log on in `Dev` or `QA` is what starts writing values (PR-006, [deployment plan](docs/aws-deployment-plan.md)).
- The test harness runs the same modes with `--detection-log on` and `--detection-log values`, and `--default-detection on|off` (off unless given) sets `Presidio:DefaultDetection` for its run.

**Harness files.** Each before/after file in `logs/PNR/` holds the block with values right after the payload hex dump, so an offset can be looked up there. Each run also writes `detection-summary.txt` beside `report.json` (WR-021): the detection mode the run used, then every review-group field across the run, by slot and payload offset, with its review reasons, character shapes, a count and the file names. It holds no string value.

### Results on the supplied samples

Both PNR samples run through the real worker and local Presidio with the shipped `Presidio:DefaultDetection` off, and both are published. The 104 values the former field policies protected in them include 16 names in scope for the names-only check: 12 are protected and 4 are approved exceptions in [parity-exceptions.json](tests/fixtures/parity-exceptions.json), standalone names in which nothing is detected (the earlier field policies masked them by their position, which detection has no equivalent for). The other 88 baseline values are out of scope (names-only) and published as received. The value-free spike summary is in [tests/fixtures/spike-summary.json](tests/fixtures/spike-summary.json). In the samples, `PNR.txt` has 14 changed strings and 57 for review, and `PNR_02.txt` 3 and 18; no line is a `spacy-guess`.

### Known limits

- Names only: with `Presidio:DefaultDetection` off, only passenger names in the shapes the file defines are protected. Dates of birth, document numbers, emails, phone numbers, tickets, loyalty numbers, payment references and addresses are published as received, as are names in free text or on their own.
- A string in which nothing is detected is published unchanged (fail open, WR-014). Presidio's documentation notes that automated detection is not guaranteed to find all sensitive information; the detection log lists every such string under Review, and `detection-summary.txt` collects them, so a recognizer can be added.
- spaCy NER guesses (default detection on only): it can detect an operational value (a PNR such as `JQWX4H` as `PERSON`, `JORDAN` in a seat remark as `LOCATION`, `1405` as `DATE_TIME`) and change it. Such lines are listed under Review as `spacy-guess`; an allow-list item keeps a value that must stay unchanged.
- Binary strings of one to three characters are published unchanged (WR-015), for example three-digit phone area codes.
- An allow-list item releases every whole string it matches, whatever it holds. With default detection on, the shipped date item (`^\d{2}[A-Z]{3}\d{2}(?:\d{2})?$`) also keeps a standalone date of birth such as `12MAR85` in its own string, and the time item (`^\d{4}$`) a standalone card suffix or approval code.
- A result longer than the text it replaces is cut (`<PERS` for a five-character name with `replace`) and listed for review; `mask` never is.
- `hash` writes the first characters of a 64- or 128-character hex digest, as many as the value is wide, and is not listed as cut. Without `salt`, each value gets a random salt, so equal values do not hash alike; with a fixed `salt`, a short value cut to a few hex characters can be guessed by trying values, so prefer `mask` for short values.
- Binary text that contains NUL bytes, such as UTF-16, is not recognised as text and is published unchanged. Producers send ASCII; the supplied exports use CCSID 437.
- A Presidio regex timeout is detected through Presidio's "timed out" warning. Recheck `test_presidio_rules` when Presidio is upgraded.

## Technology stack

| Technology | Version |
| --- | --- |
| C# / .NET | .NET 10 |
| Python.NET | 3.2.0 |
| CPython | 3.12.10 |
| Presidio Analyzer / Anonymizer | 2.2.364 |
| spaCy / en_core_web_lg | 3.8.16 / 3.8.0 |

Python runs inside the .NET worker process. Processing makes no HTTP calls or model downloads; the broker connection uses the native Solace protocol.

## Setup and run

Running from source requires Windows, the .NET 10 SDK and Python 3.12; running against a local broker also needs Docker or Podman. Run these steps in PowerShell from the project root. The [container image](#run-the-worker-in-docker) needs only Docker.

### 1. Create the Python virtual environment

Do this once during initial setup:

```powershell
py -3.12 -m venv src/.venv
```

### 2. Activate the Python virtual environment

Activate it whenever you open a new PowerShell terminal:

```powershell
.\src\.venv\Scripts\Activate.ps1
```

If PowerShell reports that running scripts is disabled, allow locally created scripts for this terminal session, then retry activation:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
.\src\.venv\Scripts\Activate.ps1
```

If prompted, enter `Y`. The policy change lasts only until this PowerShell terminal closes; administrator access is not required. See [PowerShell execution policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies).

If your organization enforces a policy that still blocks activation, use the Python launcher to install packages for its selected interpreter:

```powershell
py -m pip install -r requirements.txt
```

With an active virtual environment and no explicit Python version, `py` uses that environment. Without activation, `py` uses its default interpreter and may install packages outside `src/.venv`. If activation remains blocked, confirm the selected interpreter with `py -c "import sys; print(sys.executable)"` before installing; it should end in `src\.venv\Scripts\python.exe` for this project.

The prompt normally shows `(.venv)` after activation. Confirm Python uses this environment:

```powershell
python -c "import sys; print(sys.executable)"
```

The path should end in `src\.venv\Scripts\python.exe`.

### 3. Install the Python packages

With the virtual environment active, install the required packages:

```powershell
python -m pip install -r requirements.txt
```

### 4. Deactivate the Python virtual environment

After package installation finishes, deactivate it:

```powershell
deactivate
```

### Build

```powershell
dotnet build PresidioDemo.slnx
```

### Run locally against a Solace broker

The `Local` environment expects a Solace broker on `tcp://localhost:55555` and creates its queues on startup. Start one with Docker Desktop (WSL2 backend), then run the worker. The broker admin password comes from `$env:SOLACE_ADMIN_PASSWORD` or from a git-ignored `deploy/.env` file containing `SOLACE_ADMIN_PASSWORD=<choose one>`:

```powershell
docker compose -f deploy/docker-compose.yml up -d   # first start takes about 30 seconds
$env:SOLACE_PASSWORD = "<client password; any value on the default local broker>"
dotnet run --project src/PresidioDemo.Worker
```

Wait for `READY`, then send the samples from another terminal. The sender connects as `<ClientName>-sender`, because the broker allows one connection per client name and would otherwise disconnect the running worker:

```powershell
$env:SOLACE_PASSWORD = "<same value>"
dotnet run --project tools/FeedSampleSender
```

Protected messages are published to the `airline/pnr/protected` topic and rejected ones go to the `q.pnr.dlq` queue. A topic keeps nothing without a subscriber, so start the output reader before sending. It subscribes to each route's output topic as `<ClientName>-reader`, decodes every message with its feed's codec, and writes the published (protected) value of each decoded string to a text file under the git-ignored `.test-runs/output-reader/`:

```powershell
$env:SOLACE_PASSWORD = "<same value>"
dotnet run --project tools/FeedOutputReader                 # Ctrl+C stops; --count 2 stops after two messages
```

```text
RECV PNR    PNR.txt                    12466 bytes  fields=89
```

```text
=== MESSAGE 1 | 2026-11-16 19:10:45.188 | topic:airline/pnr/protected
  message ID: app:6b2df030ff5ddb11243d118435560d45
  file:       "PNR.txt"
  headers:    presidio-feed="PNR"
  feed PNR | format mq-export | 12466 bytes | fields=89 | offsets=payload
  Decoded fields (published values)
    slot 12 @0x01A4  "######/######"
    ...
```

The reader removes nothing from any queue and sees only messages published while it runs; `--topic <topic>` replaces the route's topic (Solace wildcards such as `airline/pnr/>` are allowed) and `--output <file>` the log file. To keep protected output while no reader runs, create a queue in the PubSub+ Manager (http://localhost:8080, user `admin`) with the subscription `airline/>` before sending. Ctrl+C stops the worker after the current messages; unacknowledged messages are redelivered on the next start. `docker compose -f deploy/docker-compose.yml down` removes the broker and its queues.

With the two PNR samples, expect 2 protected messages, no dead-lettered message, and an empty input queue. The local broker run has not been repeated on this branch; the harness gives the same result (see [Process files without a broker](#process-files-without-a-broker)).

To run a deployed environment, set `DOTNET_ENVIRONMENT` (for example `Dev`) and `SOLACE_PASSWORD`, then start `PresidioDemo.Worker`. It exits with code 1 and one message if the settings are invalid or the broker is unreachable.

### Run the worker in Docker

For a step-by-step walkthrough with expected output and troubleshooting, see the [Docker guide](docs/docker-guide.md).

The [Dockerfile](Dockerfile) builds a Linux image of the worker on the .NET 10 runtime (Ubuntu 24.04) with CPython 3.12, Presidio and the spaCy model in `/opt/venv`. The model is downloaded while the image is built, and the image sets `TLDEXTRACT_PUBLIC_SUFFIX_LIST_URLS` empty so Presidio's email recognizer uses its bundled suffix list; the running worker makes no HTTP calls. The image is about 2 GB, the container needs about 1.5 GB of memory, and startup takes about 30 seconds while the model loads.

**Local broker.** Run the broker and the worker container together; the worker starts once the broker is healthy and connects as `presidio-worker-container`:

```powershell
docker compose -f deploy/docker-compose.yml --profile worker up -d --build
docker compose -f deploy/docker-compose.yml logs -f worker   # wait for READY
```

Then send the samples from the host as above (`dotnet run --project tools/FeedSampleSender`). `docker compose -f deploy/docker-compose.yml --profile worker down` stops both. Expect 2 protected messages from the two PNR samples; the container run has not been repeated on this branch.

**Dev broker.** Copy [deploy/.env.dev.example](deploy/.env.dev.example) to `deploy/.env.dev` and replace the DemoAir stand-ins with the real Dev host, VPN, user, password, queues and topics. Put the broker's CA certificate (PEM) in `deploy/certs/`. Both are git-ignored; never commit real values. Give `Solace__ClientName` a value of your own, because the broker disconnects an existing connection that uses the same client name. The PNR route is route 0 (`Messaging__Routes__0__…`); settings that still name another feed stop startup.

```powershell
docker compose -f deploy/docker-compose.dev.yml up -d --build
docker compose -f deploy/docker-compose.dev.yml logs -f worker
```

`Dev` routes the PNR feed only; the broker's other queues are not consumed. `ProvisionQueues` is off, so the input and dead-letter queues must already exist and the user must be allowed to publish to the output topic. Before sending samples into Dev queues, confirm with the broker owner that this is allowed. To send them, load the same variables into the terminal, then run the sender:

```powershell
Get-Content deploy/.env.dev | Where-Object { $_ -match '^[A-Za-z_]+=' } | ForEach-Object { $name, $value = $_ -split '=', 2; Set-Item "env:$name" $value }
$env:DOTNET_ENVIRONMENT = "Dev"
$env:Solace__TrustStoreFolder = "$PWD/deploy/certs"
dotnet run --project tools/FeedSampleSender
```

`docker compose -f deploy/docker-compose.dev.yml down` stops the worker after the current messages; unacknowledged messages stay on the broker.

### Publish the image

[deploy/publish-image.ps1](deploy/publish-image.ps1) builds the image for `linux/amd64`, tags it with the current commit and pushes it. It refuses uncommitted changes unless you pass `-AllowDirty`, and `-DryRun` prints the commands without running them. For Amazon ECR it signs in through the AWS CLI with your AWS profile; the ECR repository must already exist. For any other registry, run `docker login` first.

```powershell
./deploy/publish-image.ps1 -Registry <account>.dkr.ecr.<region>.amazonaws.com -ExtraTag dev
./deploy/publish-image.ps1 -Registry docker.io/<user>   # after docker login
```

The script prints the pushed digest. Reference the digest or the commit tag in the task definition, because moving tags such as `dev` can change.

### Process files without a broker

The test harness sends `.txt` (IBM MQ export) and `.bin` (raw payload) files through the real worker and Presidio over the in-memory broker. It replaces the former `--process-folder` batch:

```powershell
dotnet run --project tests/FeedPipelineHarness -- --input docs/sample-docs --output .test-runs/samples
```

It writes `published/PNR/`, `dead-letter/PNR/` (with a `.reason.txt` per file), full before/after logs in `logs/PNR/`, `report.json` and `detection-summary.txt`. The exit code is 0 when every file is published and 2 when any file is rejected; the two PNR samples give 0. Files are routed by their feed folder, or by name (`PNR_02.txt`). `--recognizer-directory` selects another recognizer folder, `--detection-log on` prints the value-free detection blocks (`values` adds the values), and `--fail-publishes <n>` simulates a broker outage.

## Samples and configuration files

- `docs/sample-docs/PNR/`: immutable source files for review and testing (`PNR.txt`, `PNR_02.txt`). They aren't needed at runtime. A future negative sample would end in `_Negative_Testing` and be rejected by design; none is supplied for PNR.
- `src/PresidioDemo.Core/Configuration/recognizers/pnr.xml`: the feed's recognizer file, copied next to the worker at build time and into the image.

The harness logs have separate `BEFORE ANONYMIZATION` and `AFTER ANONYMIZATION` headings with divider lines:
- **Before, for MQ input:** the complete original feed data, the decoded payload, a plain-text payload view, then the detection block with values.
- **After:** the decoded anonymized payload, then the complete rebuilt and validated feed data.
- **Binary data:** shown as a hex dump with a readable character column.
- **Rejected messages:** the complete input and the reason.

These logs contain original personal data and are written only under `.test-runs/`, which is excluded from Git.

## Tests

| Command | What it checks | Needs |
| --- | --- | --- |
| `dotnet run --project tests/WorkerChecks` | Environment selection, settings validation (including a route for a removed feed, and `Presidio:DefaultDetection`: bound, off by default, set per environment), ack-after-publish, outage retry, redelivery, max deliveries, dead-lettering, header filtering | nothing |
| `dotnet run --project tests/Messaging.ContractTests` | The broker contract, on the in-memory broker; also on Solace when `SOLACE_CONTRACT_TESTS=1` and the local broker runs | nothing (Solace: local broker) |
| `dotnet run --project tests/FeedPipelineChecks` | Detection with real Presidio on both samples, `Presidio:DefaultDetection` off and on with and without defined recognizers, the operators and width fitting, per-string decisions and switches, fail-closed cases, names-only parity regression, scheduling and dead-lettering through `FeedWorker` | Python venv |
| `dotnet run --project tests/FeedRoundTripChecks` | Byte-exact round trips without anonymization | nothing |
| `dotnet run --project tests/GenericCodecChecks` | The generic XDR scanner and codec on both samples | nothing |
| `dotnet run --project tests/DetectionParityChecks -- config-checks` / `-- spike` | Recognizer file validation and name coverage; names-only parity of the baseline's `PERSON` values (the rest counted as out of scope), rebuilds and throughput against the frozen baseline | nothing / Python venv |
| `src/.venv/Scripts/python.exe -m unittest discover -s tests/unit-tests -p test_*.py` | The PNR samples through the harness against the baseline's names, template changes, recognizer patterns, the detection bridge with `defaultDetection` off and on, the value-free detection log and the project layout | built solution, Python venv |
| `src/.venv/Scripts/python.exe tests/run_sample_verification.py` | Both samples end to end | built solution, Python venv |

The round-trip checks load the normal codec and workflow with a keep-everything protector, and compare complete input and output bytes, decoded payloads and field values over three cycles per example. Results go to a fresh `.test-runs/round-trip-*` folder. The same checks also run in `FeedPipelineChecks`.

[Feed format analysis](docs/feed-analysis.md) · [Earlier implementation notes](../../topics/feeds-demo/v14/feed-implementation-reference-v14.md) (workspace folder outside this repository; describes the earlier folder-based version)
