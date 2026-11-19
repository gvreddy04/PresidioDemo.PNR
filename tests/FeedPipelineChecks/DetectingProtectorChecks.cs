using System.Buffers.Binary;
using System.Text;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Pipeline;
using PresidioDemo.Core.Protection;

/// <summary>
/// Checks the decoded-text protector (WR-014, WR-015): each decoded string is decided on its own, detected spans get their
/// entity's action fitted to width, a string in which nothing is detected is published unchanged and listed for review,
/// detection failures dead-letter, and every production sample rebuilds and validates. The shipped
/// <c>Presidio:DefaultDetection</c> is off, so only the recognizers a file defines act; the checks that need Presidio's
/// predefined recognizers switch it on, and the four combinations of the switch with and without defined recognizers are
/// checked on made-up strings.
/// </summary>
internal static class DetectingProtectorChecks
{

    #region Methods

    /// <summary>
    /// Runs the checks. Python must be started; the protector is configured here, after the failure-injection check.
    /// </summary>
    /// <param name="projectRoot">The repository root.</param>
    /// <returns>A task that completes when every check passed.</returns>
    public static async Task RunAsync(string projectRoot)
    {
        string recognizers = Path.Combine(projectRoot, "src", "PresidioDemo.Core", "Configuration", "recognizers");
        var files = RecognizerFileReader.ReadAll(recognizers);
        string samples = Path.Combine(projectRoot, "docs", "sample-docs");
        var protector = new DetectingFeedProtector(files);
        Check(!protector.DefaultDetection && !FeedPipelineConfiguration.Load(recognizers).CreateProtector().DefaultDetection
            && FeedPipelineConfiguration.Load(recognizers, defaultDetection: true).CreateProtector().DefaultDetection,
            "default detection is off unless Presidio:DefaultDetection switches it on");

        // Detection failure: the bridge has no registry for an unconfigured feed name, so the call fails and dead-letters.
        var unconfigured = new DetectingFeedProtector(new Dictionary<FeedType, RecognizerFile> { [FeedType.PNR] = files[FeedType.PNR] });
        await RejectAsync(() => unconfigured.ProtectAsync(FeedType.PNR, Parse(Path.Combine(samples, "PNR", "PNR.txt")), CancellationToken.None),
            "Local Presidio detection failed", "a Presidio failure before configuration dead-letters");
        protector.Configure();

        int published = 0;
        foreach (string path in Directory.EnumerateFiles(samples, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(path).Contains("Negative_Testing", StringComparison.Ordinal))
            {
                continue;
            }
            var feed = FeedFileRouting.FromPath(path);
            var codec = Codec(feed);
            var message = Message(path, feed);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            byte[] output = codec.Serialize(parsed, outcome.Values);
            codec.Validate(parsed, output);
            Check(parsed.Fields.All(f => outcome.Values[f.Id].Length == f.Value.Length), Path.GetFileName(path) + ": every protected string keeps its width");
            var decoded = parsed.Fields
                .Select(f => f.Value).Where(v => v.Any(char.IsAsciiLetterOrDigit) && DetectingFeedProtector.Shape(v) != v).ToHashSet(StringComparer.Ordinal);
            Check(outcome.Events.All(e => !decoded.Contains(e.Shape) && !decoded.Contains(e.Recognizer) && !decoded.Contains(e.Entity)),
                Path.GetFileName(path) + ": no detection event field carries a decoded value");
            // WR-020: the decoded values travel beside the events, one per slot, for the values flag only.
            var candidateValues = parsed.Fields.Select(f => f.Value);
            Check(outcome.CandidateValues is { } carried && carried.Count == outcome.Events.Count && carried.SequenceEqual(candidateValues),
                Path.GetFileName(path) + ": the candidate values match the decoded strings by slot");
            // WR-018: a binary string is located by its payload offset only.
            var payload = FeedPayloadReader.Read(message).Payload;
            Check(outcome.Events.Zip(parsed.Fields).All(pair => pair.First.Offset is int at &&
                Encoding.ASCII.GetString(payload.AsSpan(at, pair.Second.Value.Length)) == pair.Second.Value), Path.GetFileName(path) + ": each binary event's offset leads to its value");
            published++;
        }
        Console.WriteLine($"PASS {published} production samples protected, rebuilt and validated with the recognizer files");

        // WR-018, WR-020: one block per message; every string in exactly one group; review lines carry a reason and a fix.
        DetectionEvent[] events =
        [
            new(0, 13, "AAAAAA/AAAAAA", "passengerName", "PERSON", 0.9, DetectionDecision.Changed, 0x01A4, "mask"),
            new(1, 6, "AAA999", "none", "", 0, DetectionDecision.NothingDetected, 0x0018, Reasons: ReviewReason.NothingDetected, Threshold: 0.5),
            new(2, 22, "AAAA AAA", DetectingFeedProtector.SpacyRecognizer, "LOCATION", 0.85, DetectionDecision.Changed, 0x0200, "mask", ReviewReason.SpacyGuess, 0.5),
            new(3, 6, "AA9999", "allow_list", "", 0, DetectionDecision.Allowed, 0x00E0),
            new(4, 10, "AA99999999", "UsLicenseRecognizer", "US_DRIVER_LICENSE", 0.3, DetectionDecision.BelowThreshold, 0x0288, Reasons: ReviewReason.BelowThreshold, Threshold: 0.5),
            new(5, 7, "99AAA99", "dateOfBirth", "DATE_OF_BIRTH", 0.9, DetectionDecision.Changed, 0x02A0, "mask"),
            new(6, 12, "AAAA AAAAAAA", "EmailRecognizer", "EMAIL_ADDRESS", 1, DetectionDecision.Changed, 0x02C0, "replace", ReviewReason.NoAction | ReviewReason.Cut, 0.5),
        ];
        string[] fictionalBefore = ["MILLER/JORDAN", "ABC123", "LINE ONE\nSAY \"HI\" \\ X", "XY1234", "XY99887766", "14MAR85", "CALL O'NEIL"];
        string[] fictionalAfter = ["######/######", "ABC123", "######################", "XY1234", "XY99887766", "*******", "CALL <EMAIL"];
        string[] Lines(string block) => block.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        string Section(string block, string heading)
        {
            var lines = Lines(block);
            int start = Array.FindIndex(lines, line => line == "  " + heading);
            int end = Array.FindIndex(lines, start + 1, line => !line.StartsWith("    ", StringComparison.Ordinal));
            return string.Join("\n", lines[(start + 1)..(end < 0 ? lines.Length : end)]);
        }
        const string ReviewHeading = "Review (check these; each line has a fix)";
        var log = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PNR, Guid.Empty, events);
        var valued = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PNR, Guid.Empty, events, before: fictionalBefore, after: fictionalAfter);
        Check(Lines(log)[0] == "PNR " + Guid.Empty.ToString("N") + " DETECTION PUBLISHED file=- fields=7 pii=2 non-pii=1 review=4 offsets=payload",
            "the header names the feed, transaction, outcome, file (- when none), field counts and offset base");
        Check(Lines(log).Skip(1).All(line => line.StartsWith(' ')) && Lines(valued).Skip(1).All(line => line.StartsWith(' ')),
            "every line after the header is indented, so a collector keeps the block as one entry");
        string pii = Section(log, "PII fields (changed)"), nonPii = Section(log, "Non-PII fields (unchanged)"), review = Section(log, ReviewHeading);
        Check(new[] { "@0x01A4", "@0x02A0" }.All(pii.Contains) && nonPii.Contains("@0x00E0", StringComparison.Ordinal)
            && new[] { "@0x0018", "@0x0200", "@0x0288", "@0x02C0" }.All(review.Contains), "each string is listed in its group, located by slot and offset");
        Check(Lines(log).Count(line => line.Contains(" @0x", StringComparison.Ordinal)) == events.Length, "each string appears exactly once");
        string ReviewLine(string at) => review.Split('\n').Single(line => line.Contains(at, StringComparison.Ordinal));
        Check(ReviewLine("@0x0018").Contains("  unchanged  ", StringComparison.Ordinal)
            && ReviewLine("@0x0018").Contains("reason=nothing-detected fix=\"add a recognizer if this is personal data, or an allow-list item if it is not\"", StringComparison.Ordinal),
            "a nothing-detected string is listed unchanged with its fix");
        Check(ReviewLine("@0x0288").Contains("score=0.30<0.50", StringComparison.Ordinal) && ReviewLine("@0x0288").Contains("reason=below-threshold", StringComparison.Ordinal),
            "a below-threshold string compares its score with the threshold");
        Check(ReviewLine("@0x0200").Contains("  changed  ", StringComparison.Ordinal) && ReviewLine("@0x0200").Contains("operator=mask decision=changed reason=spacy-guess", StringComparison.Ordinal),
            "a spaCy guess is listed for review although it changed the string");
        Check(ReviewLine("@0x02C0").Contains("reason=no-action+cut fix=\"add an <anonymizer> for EMAIL_ADDRESS; use mask for this entity, or a shorter new_value\"", StringComparison.Ordinal),
            "several reasons are listed with one fix each");
        Check(fictionalBefore.All(value => !log.Contains(value, StringComparison.Ordinal)) && fictionalAfter.Where(value => !value.All(c => c == '#')).All(value => !log.Contains(value, StringComparison.Ordinal))
            && !log.Contains("->", StringComparison.Ordinal) && log.Contains("len=13 shape=AAAAAA/AAAAAA", StringComparison.Ordinal),
            "a value-free block carries lengths and shapes, never a value");
        Check(Lines(valued).Length == Lines(log).Length, "an encoded value never breaks a line");
        Check(valued.Contains("\"MILLER/JORDAN\" -> \"######/######\"", StringComparison.Ordinal) && valued.Contains("\"CALL O'NEIL\" -> \"CALL <EMAIL\"", StringComparison.Ordinal)
            && Section(valued, "Non-PII fields (unchanged)").Contains("\"XY1234\"  recognizer=allow_list", StringComparison.Ordinal),
            "a changed string shows before -> after; an unchanged string shows its value once");
        string encoded = Lines(valued).Single(line => line.Contains("@0x0200", StringComparison.Ordinal));
        string quoted = encoded[(encoded.IndexOf('"', StringComparison.Ordinal))..encoded.IndexOf(" -> ", StringComparison.Ordinal)];
        Check(System.Text.Json.JsonSerializer.Deserialize<string>(quoted) == fictionalBefore[2], "an encoded value reads back exactly");
        var rejected = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PNR, Guid.Empty, events, "Fixture rejection.", fictionalBefore, null);
        Check(Lines(rejected)[0].EndsWith(" DETECTION DEAD-LETTERED file=- fields=7 pii=2 non-pii=1 review=4 offsets=payload reason=\"Fixture rejection.\"", StringComparison.Ordinal)
            && rejected.Contains("\"MILLER/JORDAN\" -> not published", StringComparison.Ordinal), "a dead-lettered block gives the reason and no after value");
        var allowedOnly = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PNR, Guid.Empty, [events[3]]);
        Check(Section(allowedOnly, "PII fields (changed)") == "    (none)" && Section(allowedOnly, ReviewHeading) == "    (none)",
            "an empty group is printed as (none)");
        // Review R-003: fewer values than events must not fail; a slot without a value keeps the value-free form.
        var shortValues = PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PNR, Guid.Empty, events, before: ["MILLER/JORDAN"], after: fictionalAfter);
        Check(shortValues.Contains("\"MILLER/JORDAN\" -> \"######/######\"", StringComparison.Ordinal) && shortValues.Contains("len=6 shape=AAA999", StringComparison.Ordinal),
            "a slot without a value keeps the value-free form");
        // Review R-010: zero-width and bidirectional formatting characters are escaped, and the value still reads back.
        string disguised = "CALL " + (char)0x202E + "MILLER" + (char)0x200B + " HOME";
        string encodedDisguise = PresidioDemo.Core.Logging.DetectionLog.Encode(disguised);
        Check(!encodedDisguise.Contains((char)0x202E) && !encodedDisguise.Contains((char)0x200B) && encodedDisguise.Contains("\\u202E", StringComparison.Ordinal)
            && System.Text.Json.JsonSerializer.Deserialize<string>(encodedDisguise) == disguised, "formatting characters are escaped and the value reads back");
        Check(PresidioDemo.Core.Logging.DetectionLog.Encode("O'NEIL" + (char)0x2028 + "X") == "\"O'NEIL\\u2028X\"", "an apostrophe stays readable and a line separator is escaped");
        // Review R-011: the worker writes each block with one Console.Out.Write. Console.Out is a synchronized writer, so
        // blocks written from many threads at once (as by several consumers per task) must come out whole.
        Check(Console.Out.GetType().Name == "SyncTextWriter", "the console writer the worker uses is synchronized");
        var captured = new StringWriter();
        var console = Console.Out;
        Console.SetOut(TextWriter.Synchronized(captured));
        try
        {
            Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                Console.Out.Write(PresidioDemo.Core.Logging.DetectionLog.FormatBlock(FeedType.PNR, new Guid(i, 0, 0, new byte[8]), events, before: fictionalBefore, after: fictionalAfter)));
        }
        finally
        {
            Console.SetOut(console);
        }
        var written = Lines(captured.ToString());
        int perBlock = Lines(valued).Length;
        // "PNR " plus the 32-digit transaction and a space: the rest of each header, and every body line, must match.
        const int afterTransaction = 37;
        Check(written.Length == 400 * perBlock && Enumerable.Range(0, 400).All(b =>
            written[b * perBlock][afterTransaction..] == Lines(valued)[0][afterTransaction..]
            && written.Skip(b * perBlock + 1).Take(perBlock - 1).SequenceEqual(Lines(valued).Skip(1))),
            "400 blocks written from 16 threads at once each come out whole");
        Console.WriteLine("PASS the detection log writes one grouped block per message, with a fix per review line and values only when passed (WR-018, WR-020)");

        // WR-014 on the shipped file with default detection off: declared recognizers act, allowed strings and undetected
        // strings stay unchanged, and no predefined recognizer decides anything.
        string pnrSample = Path.Combine(samples, "PNR", "PNR.txt");
        var pnrOutcome = await protector.ProtectAsync(FeedType.PNR, Parse(pnrSample), CancellationToken.None);
        Check(pnrOutcome.Events.Any(e => e.Decision == DetectionDecision.Changed) && pnrOutcome.Events.Any(e => e.Decision == DetectionDecision.Allowed)
            && pnrOutcome.Events.Any(e => e.Decision == DetectionDecision.NothingDetected), "PNR.txt has changed, allowed and undetected strings");
        Check(pnrOutcome.Events.Where(e => e.Decision is DetectionDecision.Allowed or DetectionDecision.NothingDetected or DetectionDecision.BelowThreshold)
            .All(e => pnrOutcome.Values[Parse(pnrSample).Fields[e.Slot].Id] == Parse(pnrSample).Fields[e.Slot].Value), "allowed and undetected strings are published unchanged");
        var defined = files[FeedType.PNR].Recognizers.Select(r => r.Name).Concat((files[FeedType.PNR].LoneNames ?? []).Select(r => r.Name))
            .Concat(["allow_list", "none"]).ToHashSet(StringComparer.Ordinal);
        Check(pnrOutcome.Events.All(e => e.Recognizer.Split('+').All(defined.Contains) && !e.Reasons.HasFlag(ReviewReason.SpacyGuess)),
            "with default detection off every decision names a defined recognizer, and nothing is a spacy-guess");
        // AC-011: no PNR anchor; a message whose creation time is gone still publishes.
        var noCreationTime = ParseEdited(pnrSample, FeedType.PNR, (fields, payload) =>
            OverwriteFirst(payload, fields.First(f => f.Value.Length == 9 && f.Value.All(char.IsAsciiDigit)), 'A'));
        var stillPublished = await protector.ProtectAsync(FeedType.PNR, noCreationTime, CancellationToken.None);
        Check(stillPublished.Events.Count == noCreationTime.Fields.Count, "a message without a creation time is protected, not dead-lettered");

        // Made-up strings through the shipped file (AC-004, AC-005, AC-013), default detection off.
        var shipped = await ProtectStringsAsync(protector, "MILLER/JORDAN MR", "Please book a window seat", "1405", "RBY09NOV*APPR CC VI 1234 OK",
            "Contact Alex Sample", "DOCSDAHK1/P/USA/X1234567/USA/12MAR85/F/09NOV31/MILLER/JORDAN");
        Check(shipped[0] == "****************", "a declared recognizer masks MILLER/JORDAN MR");
        Check(shipped[1] == "Please book a window seat", "a string in which nothing is detected is published unchanged");
        Check(shipped[2] == "1405", "a whole-string allow-list item keeps 1405");
        Check(shipped[3] == "RBY09NOV*APPR CC VI **** OK", "an allow-list item never releases a detected piece inside a longer string");
        Check(shipped[4] == "Contact Alex Sample", "no spaCy PERSON guess runs with default detection off");
        Check(!shipped[5].Contains("12MAR85", StringComparison.Ordinal) && !shipped[5].Contains("09NOV31", StringComparison.Ordinal)
            && shipped[5].StartsWith("DOCSDAHK1/P/", StringComparison.Ordinal) && shipped[5].EndsWith("/*************", StringComparison.Ordinal),
            "a DOCS birth date, expiry and name are masked although the dates look like allowed dates");
        Console.WriteLine("PASS the shipped file acts per string: declared recognizers, allow list, unchanged when nothing is detected");

        // The fields of the PNR field list on made-up strings: IP address, phone remark, agency number, OSI nationality and
        // change identifiers.
        var fields = await ProtectStringsAsync(protector, "CLIENT IP 10.20.30.40", "5555550142*C 1.2", "55*B 1.2", "QQQ1X/DA9Z/12345678/DFW/1X/T/US/USD/AS",
            "PAX1.2 CA NATIONAL // CA RESIDENT", "1A2B3C", "131500", "123456", "K7QX2M");
        Check(fields[0] == "CLIENT IP ***********", "an IPv4 address is masked with default detection off");
        Check(fields[1] == "***********C 1.2" && fields[2] == "***B 1.2", "a phone remark masks the digits and keeps the passenger marker");
        Check(fields[3] == "QQQ1X/DA9Z/********/DFW/1X/T/US/USD/AS", "the agency number of a received-from line is masked, and nothing else");
        Check(fields[4] == "PAX1.2 ## NATIONAL // ###########", "the nationality and residence in OSI text are masked");
        Check(fields[5] == "******" && fields[6] == "131500" && fields[7] == "123456" && fields[8] == "K7QX2M",
            "a change identifier needs a digit and a letter A-F: a time, zeros and a record locator stay unchanged");
        Console.WriteLine("PASS the IP, phone remark, agency number, OSI nationality and change identifier recognizers mask their fields");

        // passengerName no longer masks codes shaped like SURNAME/GIVEN (made-up stand-ins for shapes seen in the samples).
        var codes = await ProtectOutcomeAsync(protector, "QQQ1X/DA9Z/12345678/DFW/1X/T/US/USD/AS", "T/FQR/MPDA123456/12000/0/01612345678/0002//",
            "XXCOM-US/DA123456-REF/", "RMMPDANN1DFWORD1234X09NOV.YQX/S25K-W-Y", "ABC OP/MILEAGE QUOTED 12000", "DOCSDA///JORDAN");
        Check(codes.Events.All(e => !e.Recognizer.Split('+').Contains("passengerName") || e.Slot == 5), "no code shaped like SURNAME/GIVEN is read as a name");
        Check(codes.Values["1"].StartsWith("T/FQR/MP", StringComparison.Ordinal) && codes.Values["2"].StartsWith("XXCOM-US/", StringComparison.Ordinal)
            && codes.Values["3"] == "RMMPDANN1DFWORD1234X09NOV.YQX/S25K-W-Y" && codes.Values["4"] == "ABC OP/MILEAGE QUOTED 12000",
            "their code letters stay readable");
        Check(codes.Values["5"] == "DOCSDA///******", "in a DOCS record with empty fields the name is masked, not the record code");
        Console.WriteLine("PASS passengerName masks whole names, passenger references and DOCS names only");

        // Lone names (type="lone_name"): a lone string equal to a name part found in the same message is masked whole.
        var lone = await ProtectOutcomeAsync(protector, "MILLER", "JORDAN", "DOCSDAHK1/////12MAR85/F//MILLER/JORDAN", "SILVER", "DENVER", " jordan ", "MR");
        Check(lone.Values["0"] == "******" && lone.Values["1"] == "******" && lone.Values["5"] == " ****** "
            && lone.Events[0].Recognizer == "loneNamePart" && lone.Events[0].Entity == "PERSON",
            "a lone name part of a passengerName match in the same message is masked, also with other case and spaces");
        Check(lone.Values["3"] == "SILVER" && lone.Values["4"] == "DENVER" && lone.Values["6"] == "MR", "other lone words and titles stay unchanged");
        var alone = await ProtectStringsAsync(protector, "MILLER", "JORDAN");
        Check(alone.SequenceEqual(["MILLER", "JORDAN"]), "without a passengerName match in the message, a lone word is not a name");
        Console.WriteLine("PASS a lone name part is masked only when passengerName finds the name in the same message");

        // AC-003, AC-006, AC-008: test-only files reconfigure the PNR registry, so the shipped one is restored afterwards.
        string Temporary(string content)
        {
            string file = Path.Combine(Path.GetTempPath(), "pnr-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(file, content);
            return file;
        }
        DetectingFeedProtector Configured(string file, bool defaultDetection = false)
        {
            var configured = new DetectingFeedProtector(new Dictionary<FeedType, RecognizerFile> { [FeedType.PNR] = RecognizerFileReader.Read(file, FeedType.PNR) },
                defaultDetection);
            configured.Configure();
            return configured;
        }
        string empty = Temporary("<recognizers feed=\"PNR\" />");
        string noCards = Temporary("<recognizers feed=\"PNR\"><recognizer name=\"CreditCardRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<anonymizer entity=\"PERSON\" type=\"mask\" /></recognizers>");
        string withDefault = Temporary("<recognizers feed=\"PNR\"><anonymizer entity=\"PERSON\" type=\"mask\" /><anonymizer entity=\"DEFAULT\" type=\"mask\" /></recognizers>");
        string shippedFile = Path.Combine(recognizers, RecognizerFileReader.FileName(FeedType.PNR));
        try
        {
            // Presidio:DefaultDetection with and without defined recognizers (made-up strings).
            string[] madeUp = ["CTCEDAHK1 alex@example.com -1MILLER/JORDAN", "Contact Alex Sample", "Card 4111 1111 1111 1111", "Please book a window seat"];
            var definedOnly = await ProtectStringsAsync(protector, madeUp);
            Check(definedOnly[0] == "CTCEDAHK1 alex@example.com -1*************" && definedOnly[1] == madeUp[1] && definedOnly[2] == madeUp[2],
                "default detection off with recognizers: only the defined ones act; the made-up email stays and the SURNAME/GIVEN name is masked");
            var both = await ProtectOutcomeAsync(Configured(shippedFile, defaultDetection: true), madeUp);
            Check(both.Values["0"] == "CTCEDAHK1 **************** -1*************" && both.Values["1"] == "Contact ***********"
                && both.Values["2"] == "Card " + new string('#', 19) && both.Values["3"] == madeUp[3],
                $"default detection on with recognizers: both act; the email gets its own anonymizer, else DEFAULT, and Presidio's PERSON the PERSON anonymizer (got {string.Join(" | ", both.Values.Values)})");
            Check(both.Events[0].Recognizer.Split('+').Contains("passengerName") && both.Events[0].Recognizer.Split('+').Contains("EmailRecognizer")
                && both.Events[1].Reasons.HasFlag(ReviewReason.SpacyGuess), "default detection on: the log names both kinds of recognizer, and a spaCy guess is for review");
            var presidioOnly = await ProtectStringsAsync(Configured(empty, defaultDetection: true), madeUp);
            Check(presidioOnly[0].StartsWith("CTCEDAHK1 <EMAIL_ADDRESS>  -1MILLER/", StringComparison.Ordinal) && presidioOnly[1] == "Contact <PERSON>   " && presidioOnly[3] == madeUp[3],
                $"default detection on without recognizers: only Presidio acts (got {string.Join(" | ", presidioOnly)})");
            var nothingInput = Parse(pnrSample);
            var nothing = await Configured(empty).ProtectAsync(FeedType.PNR, nothingInput, CancellationToken.None);
            var nothingStrings = await ProtectStringsAsync(Configured(empty), madeUp);
            byte[] pnrBytes = File.ReadAllBytes(pnrSample);
            Check(nothingStrings.SequenceEqual(madeUp) && nothing.Events.All(e => e.Decision is DetectionDecision.NothingDetected or DetectionDecision.Allowed)
                && Codec(FeedType.PNR).Serialize(nothingInput, nothing.Values).AsSpan().SequenceEqual(pnrBytes),
                "default detection off without recognizers: the output equals the input, byte for byte");
            Console.WriteLine("PASS Presidio:DefaultDetection: off runs only the defined recognizers (none: output equals input); on adds Presidio's predefined recognizers");

            var bare = await ProtectStringsAsync(Configured(empty, defaultDetection: true), "Please book a window seat", "Contact Alex Sample", "alex@example.com", "Card 4111 1111 1111 1111");
            Check(bare[0] == "Please book a window seat", "an emptied file starts and publishes an undetected string unchanged");
            Check(bare[1] == "Contact <PERSON>   " && bare[2] == "<EMAIL_ADDRESS> " && bare[3] == "Card " + "<CREDIT_CARD>".PadRight(19),
                "with default detection on and nothing configured Presidio's predefined recognizers detect, and replace is fitted to width");
            var switchedOff = await ProtectStringsAsync(Configured(noCards, defaultDetection: true), "Card 4111 1111 1111 1111", "Contact Alex Sample", "alex@example.com");
            Check(switchedOff[0] == "Card 4111 1111 1111 1111", "a predefined recognizer switched off detects nothing");
            Check(switchedOff[1] == "Contact ***********" && switchedOff[2] == "<EMAIL_ADDRESS> ", "an entity uses its own anonymizer, else replace");
            var fallback = await ProtectStringsAsync(Configured(withDefault, defaultDetection: true), "alex@example.com");
            Check(fallback[0] == "****************", "an entity without its own anonymizer uses DEFAULT");
            Console.WriteLine("PASS with default detection on an emptied file runs Presidio's defaults; switches and anonymizers change the output with no code change");

            // Review reasons as the protector produces them (made-up strings), with default detection on.
            var guess = await ProtectOutcomeAsync(Configured(withDefault, defaultDetection: true), "Contact Alex Sample", "alex@example.com", "LI WU");
            Check(guess.Events[0].Reasons.HasFlag(ReviewReason.SpacyGuess) && !guess.Events[0].Reasons.HasFlag(ReviewReason.NoAction),
                "a spaCy PERSON with its own anonymizer is a spacy-guess, not a no-action");
            Check(guess.Events[1].Reasons.HasFlag(ReviewReason.NoAction) && guess.Events[1].NoActionEntities.Contains("EMAIL_ADDRESS", StringComparison.Ordinal),
                "an entity that used DEFAULT is a no-action naming that entity");
            var mixed = await ProtectOutcomeAsync(Configured(noCards, defaultDetection: true), "Contact Alex Sample at alex@example.com");
            Check(mixed.Events[0].NoActionEntities.Contains("EMAIL_ADDRESS", StringComparison.Ordinal) && !mixed.Events[0].NoActionEntities.Contains("PERSON", StringComparison.Ordinal),
                "the no-action fix names only the entities without their own anonymizer");
            var cut = await ProtectOutcomeAsync(Configured(empty, defaultDetection: true), "LI WU");
            Check(cut.Values["0"] == "<PERS" && cut.Events[0].Reasons.HasFlag(ReviewReason.Cut), "a replace result longer than the value is cut and listed as cut");
        }
        finally
        {
            File.Delete(empty);
            File.Delete(noCards);
            File.Delete(withDefault);
            protector.Configure();
        }

        // Overlaps, keep and hash with custom recognizers (made-up codes): CODE_A covers AB1234 at 0.9 and CODE_B covers
        // 1234CD at 0.7, so CODE_A wins the shared 1234 and CODE_B keeps CD. chars_to_mask counts from CODE_B's own start,
        // so masking its first two characters changes only what CODE_A already took. Default detection is off, and the file
        // switches spaCy off too, because with it on spaCy reads a made-up code such as AB1234CD as a PERSON and would win
        // the whole string.
        string Codes(string actionA, string actionB) => Temporary("<recognizers feed=\"PNR\">"
            + "<recognizer name=\"CryptoRecognizer\" type=\"predefined\" enabled=\"true\" />"
            + "<recognizer name=\"SpacyRecognizer\" type=\"predefined\" enabled=\"false\" />"
            + "<recognizer name=\"codeA\" type=\"custom\" supported_entity=\"CODE_A\"><pattern name=\"a\" score=\"0.9\">AB\\d{4}</pattern></recognizer>"
            + "<recognizer name=\"codeB\" type=\"custom\" supported_entity=\"CODE_B\"><pattern name=\"b\" score=\"0.7\">\\d{4}CD</pattern></recognizer>"
            + "<anonymizer entity=\"CODE_A\" " + actionA + " /><anonymizer entity=\"CODE_B\" " + actionB + " /><anonymizer entity=\"DEFAULT\" type=\"mask\" /></recognizers>");
        string overlap = Codes("type=\"mask\"", "type=\"mask\" masking_char=\"#\"");
        string partial = Codes("type=\"mask\"", "type=\"mask\" masking_char=\"#\" chars_to_mask=\"2\"");
        string keepFile = Codes("type=\"keep\"", "type=\"mask\"");
        string hashed = Codes("type=\"hash\" salt=\"demo\"", "type=\"mask\"");
        string typo = Temporary("<recognizers feed=\"PNR\"><recognizer name=\"CreditCardRecogniser\" type=\"predefined\" /></recognizers>");
        try
        {
            var both = await ProtectOutcomeAsync(Configured(overlap), "AB1234CD");
            Check(both.Values["0"] == "******##" && both.Events[0].Reasons.HasFlag(ReviewReason.Overlap), $"the higher score wins an overlap; the loser masks its own characters (got {both.Values["0"]}, {both.Events[0].Recognizer}, {both.Events[0].Entity}, {both.Events[0].Reasons})");
            var counted = await ProtectOutcomeAsync(Configured(partial), "AB1234CD");
            Check(counted.Values["0"] == "******CD", "chars_to_mask counts from the losing span's own start");
            var keep = await ProtectOutcomeAsync(Configured(keepFile), "AB1234");
            Check(keep.Values["0"] == "AB1234" && keep.Events[0].Decision == DetectionDecision.Kept && keep.Events[0].Reasons == ReviewReason.None,
                "the keep operator publishes a detected value unchanged as kept, not for review");
            var hash = await ProtectOutcomeAsync(Configured(hashed), "AB1234");
            Check(hash.Values["0"].Length == 6 && hash.Values["0"] != "AB1234" && !hash.Events[0].Reasons.HasFlag(ReviewReason.Cut),
                "a hash cut to width is not listed as cut");
            try
            {
                Configured(typo);
                throw new Exception("an unknown predefined recognizer name must stop startup");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("CreditCardRecogniser", StringComparison.Ordinal))
            {
            }
            Console.WriteLine("PASS overlaps, chars_to_mask, keep and hash behave per span; an unknown predefined name stops startup and is named");
        }
        finally
        {
            foreach (string file in new[] { overlap, partial, keepFile, hashed, typo })
            {
                File.Delete(file);
            }
            protector.Configure();
        }

        // WR-020: a rejection after detection keeps its events and values, with the reason unchanged. A codec whose
        // rebuild check fails stands in for a rebuild that differs outside the protected strings (WR-016).
        var failingRebuild = FeedWorkflowRouter.Create(protector, _ => new RebuildRejectingCodec(new PnrDecoder()));
        var afterDetection = await new FeedMessageProcessor(failingRebuild)
            .ProcessAsync(FeedType.PNR, Guid.NewGuid(), "PNR.txt", File.ReadAllBytes(pnrSample), CancellationToken.None);
        Check(!afterDetection.Published && afterDetection.Reason == RebuildRejectingCodec.Reason
            && afterDetection.DetectionEvents is { Count: > 0 } kept && afterDetection.DetectionValues?.Count == kept.Count,
            "a message dead-lettered after detection keeps its events and values, with the same reason");
        Console.WriteLine("PASS a message dead-lettered after detection keeps its events and values for the detection log (WR-020)");

        // WR-020: a message rejected before detection has no events to log.
        var reviewed = new FeedMessageProcessor(FeedPipelineConfiguration.Load(recognizers).CreateRouter(protector));
        var before = await reviewed.ProcessAsync(FeedType.PNR, Guid.NewGuid(), "truncated.bin", new byte[] { 0, 0, 0 }, CancellationToken.None);
        Check(!before.Published && before.DetectionEvents is null && before.DetectionValues is null, "truncated.bin: rejected before detection, so nothing to log");
        Console.WriteLine("PASS messages rejected before detection carry no events or values");

        // R-003: a recognizer pattern that times out dead-letters instead of publishing unchecked text. This reconfigures
        // the PNR registry, so it restores the shipped configuration afterwards.
        string slow = Temporary(File.ReadAllText(Path.Combine(recognizers, "pnr.xml")).Replace("</recognizers>",
            "<recognizer name=\"slowCheck\" type=\"custom\" supported_entity=\"DEMO_CODE\"><pattern name=\"slow\" score=\"0.85\">(A|AA)+$</pattern></recognizer></recognizers>",
            StringComparison.Ordinal));
        try
        {
            var slowProtector = Configured(slow);
            var slowInput = ParseEdited(pnrSample, FeedType.PNR, (fields, payload) =>
            {
                var target = fields.First(f => f.Value.Length >= 30 && f.Value.All(c => c is >= ' ' and <= '~'));
                Encoding.ASCII.GetBytes(new string('A', target.Value.Length - 1) + "!").CopyTo(payload, Locate(payload, target.Value));
            });
            await RejectAsync(() => slowProtector.ProtectAsync(FeedType.PNR, slowInput, CancellationToken.None),
                "Local Presidio detection failed", "a pattern timeout dead-letters the message");
            // Default detection off and no custom recognizer: the message is never sent to Presidio, so the slow pattern
            // configured there cannot run and the strings are published as received; with default detection on the same
            // file is sent, and the timeout dead-letters it.
            var undefinedFile = new Dictionary<FeedType, RecognizerFile> { [FeedType.PNR] = files[FeedType.PNR] with { Recognizers = [] } };
            var notSent = await new DetectingFeedProtector(undefinedFile).ProtectAsync(FeedType.PNR, slowInput, CancellationToken.None);
            Check(slowInput.Fields.All(f => notSent.Values[f.Id] == f.Value),
                "default detection off without custom recognizers: the message is not sent to Presidio and is published as received");
            await RejectAsync(() => new DetectingFeedProtector(undefinedFile, defaultDetection: true).ProtectAsync(FeedType.PNR, slowInput, CancellationToken.None),
                "Local Presidio detection failed", "default detection on without custom recognizers: the message is sent to Presidio");
        }
        finally
        {
            File.Delete(slow);
            protector.Configure();
        }

        // V-12: raw words such as numeric IDs are never decoded or changed, so a message whose raw word differs is
        // published with that word exactly as received.
        var pnrCodec = new PnrDecoder();
        var pnrMessage = Message(pnrSample, FeedType.PNR);
        var pnrEnvelope = FeedPayloadReader.Read(pnrMessage);
        var pnrCandidates = XdrFeedDecoder.Candidates(pnrCodec.Parse(pnrMessage));
        int firstEnd = pnrCandidates[0].Offset + pnrCandidates[0].Length;
        int rawWord = Enumerable.Range(0, pnrEnvelope.Payload.Length / 4).Select(i => i * 4)
            .First(o => o > firstEnd && !pnrCandidates.Any(c => o + 4 > c.Offset - 4 && o < c.Offset + c.Length + 4) &&
                BinaryPrimitives.ReadUInt32BigEndian(pnrEnvelope.Payload.AsSpan(o, 4)) is > 1 and < 100_000_000);
        byte[] mismatched = pnrEnvelope.Payload.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(mismatched.AsSpan(rawWord, 4), BinaryPrimitives.ReadUInt32BigEndian(mismatched.AsSpan(rawWord, 4)) + 1);
        var mismatchMessage = new FeedMessage(pnrMessage.Delivery, pnrEnvelope.Serialize(mismatched));
        var mismatchParsed = pnrCodec.Parse(mismatchMessage);
        var mismatchOutcome = await protector.ProtectAsync(FeedType.PNR, mismatchParsed, CancellationToken.None);
        byte[] mismatchOutput = pnrCodec.Serialize(mismatchParsed, mismatchOutcome.Values);
        pnrCodec.Validate(mismatchParsed, mismatchOutput);
        Check(FeedPayloadReader.Read(new FeedMessage(pnrMessage.Delivery, mismatchOutput)).Payload.AsSpan(rawWord, 4).SequenceEqual(mismatched.AsSpan(rawWord, 4)),
            "PNR raw word published unchanged");
        Console.WriteLine("PASS a PNR message with a changed raw word is published with the word unchanged");

        // WR-019: narrowing the name recognizer breaks parity with the frozen baseline before merge. This reconfigures the
        // PNR registry, so it runs last and restores the shipped configuration afterwards.
        string narrowed = Temporary(System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(recognizers, "pnr.xml")),
            "(<recognizer name=\"passengerName\"[^>]*>).*?(</recognizer>)", "$1<pattern name=\"narrow\" score=\"0.9\">[A-Z]{30,}/[A-Z]{30,}</pattern>$2",
            System.Text.RegularExpressions.RegexOptions.Singleline));
        try
        {
            int reviewedMisses = await ParityMissesAsync(protector, projectRoot, FeedType.PNR, files[FeedType.PNR].DefinedEntities);
            Check(reviewedMisses == 0, "the shipped recognizers must have no unapproved PNR parity miss");
            int narrowedMisses = await ParityMissesAsync(Configured(narrowed), projectRoot, FeedType.PNR, files[FeedType.PNR].DefinedEntities);
            Check(narrowedMisses > 0, "a narrowed name recognizer must fail parity");
            Console.WriteLine($"PASS a narrowed PNR name recognizer fails parity with the frozen baseline ({narrowedMisses} unapproved misses)");
        }
        finally
        {
            File.Delete(narrowed);
            protector.Configure();
        }
    }

    /// <summary>
    /// Protects made-up strings as one parsed message and returns the published values in order.
    /// </summary>
    private static async Task<string[]> ProtectStringsAsync(DetectingFeedProtector protector, params string[] values)
    {
        var outcome = await ProtectOutcomeAsync(protector, values);
        return Enumerable.Range(0, values.Length).Select(i => outcome.Values[i.ToString(System.Globalization.CultureInfo.InvariantCulture)]).ToArray();
    }

    /// <summary>
    /// Protects made-up strings as one parsed message; field identifiers are the slot numbers.
    /// </summary>
    private static ValueTask<DetectionOutcome> ProtectOutcomeAsync(DetectingFeedProtector protector, params string[] values) =>
        protector.ProtectAsync(FeedType.PNR, new ParsedFeed(values.Select((value, i) =>
            new FeedField(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "slot" + i, value)).ToArray(), new object()), CancellationToken.None);

    /// <summary>
    /// Counts baseline values in scope of one binary feed that the protector publishes unchanged and that are not approved
    /// exceptions (WR-019). In scope are the values whose entity a recognizer the shipped file defines produces; every other
    /// baseline value is out of scope. Value-free: compares bytes at the frozen locations only.
    /// </summary>
    /// <param name="protector">The protector to measure.</param>
    /// <param name="projectRoot">The repository root.</param>
    /// <param name="feed">A binary feed.</param>
    /// <param name="scope">The entities in scope: those the shipped file's recognizers produce.</param>
    /// <returns>The number of unapproved misses.</returns>
    private static async Task<int> ParityMissesAsync(DetectingFeedProtector protector, string projectRoot, FeedType feed, IReadOnlySet<string> scope)
    {
        using var baseline = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-baseline.json")));
        using var approved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, "tests", "fixtures", "parity-exceptions.json")));
        var exceptions = approved.RootElement.EnumerateArray().Select(e => (e.GetProperty("sample").GetString(), e.GetProperty("location").GetString())).ToHashSet();
        string folder = FeedCatalog.Get(feed).FolderName;
        int misses = 0;
        foreach (var group in baseline.RootElement.GetProperty("protected").EnumerateArray()
            .Where(e => e.GetProperty("feed").GetString() == folder && scope.Contains(e.GetProperty("entity").GetString()!))
            .GroupBy(e => e.GetProperty("sample").GetString()!))
        {
            string path = Path.Combine(projectRoot, "docs", "sample-docs", folder, group.Key);
            var codec = FeedCodecRegistry.Create(feed);
            var message = Message(path, feed);
            var parsed = codec.Parse(message);
            var outcome = await protector.ProtectAsync(feed, parsed, CancellationToken.None);
            byte[] before = FeedPayloadReader.Read(message).Payload;
            byte[] after = FeedPayloadReader.Read(new FeedMessage(message.Delivery, codec.Serialize(parsed, outcome.Values))).Payload;
            foreach (var entry in group)
            {
                int offset = int.Parse(entry.GetProperty("location").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                int length = entry.GetProperty("length").GetInt32();
                // Only characters that were not already * need to change; a value that is all * was masked before it arrived.
                bool caught = Enumerable.Range(offset, length).Where(i => before[i] != (byte)'*').All(i => before[i] != after[i]);
                if (!caught && !exceptions.Contains((group.Key, entry.GetProperty("location").GetString())))
                {
                    misses++;
                }
            }
        }
        return misses;
    }

    /// <summary>
    /// Returns the codec the decoded-text path uses for a feed.
    /// </summary>
    private static IFeedCodec Codec(FeedType feed) => FeedCodecRegistry.Create(feed);

    /// <summary>
    /// Builds a message from a sample file.
    /// </summary>
    private static FeedMessage Message(string path, FeedType feed) => new(new FeedDelivery(feed, Guid.NewGuid(), Path.GetFileName(path), path), File.ReadAllBytes(path));

    /// <summary>
    /// Parses a sample with the decoded-text codec of its feed.
    /// </summary>
    private static ParsedFeed Parse(string path)
    {
        var feed = FeedFileRouting.FromPath(path);
        return Codec(feed).Parse(Message(path, feed));
    }

    /// <summary>
    /// Parses a binary sample after editing its payload bytes in place (same width), as a changed producer would.
    /// </summary>
    private static ParsedFeed ParseEdited(string path, FeedType feed, Action<IReadOnlyList<FeedField>, byte[]> edit)
    {
        var message = Message(path, feed);
        var codec = FeedCodecRegistry.Create(feed);
        var parsed = codec.Parse(message);
        var envelope = FeedPayloadReader.Read(message);
        byte[] payload = envelope.Payload.ToArray();
        edit(parsed.Fields, payload);
        return codec.Parse(new FeedMessage(message.Delivery, envelope.Serialize(payload)));
    }

    /// <summary>
    /// Replaces the first character of a candidate's text in the payload.
    /// </summary>
    private static void OverwriteFirst(byte[] payload, FeedField field, char to) => payload[Locate(payload, field.Value)] = (byte)to;

    /// <summary>
    /// Finds a candidate's text in the payload.
    /// </summary>
    private static int Locate(byte[] payload, string value) => payload.AsSpan().IndexOf(Encoding.ASCII.GetBytes(value));

    /// <summary>
    /// Requires an InvalidDataException whose message contains the expected text.
    /// </summary>
    private static async Task RejectAsync(Func<ValueTask<DetectionOutcome>> action, string expected, string label)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal))
        {
            Console.WriteLine("PASS " + label);
            return;
        }
        throw new Exception("Expected rejection: " + label);
    }

    /// <summary>
    /// Requires a condition.
    /// </summary>
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
    }

    #endregion
}

/// <summary>
/// Wraps a real codec but fails its rebuild check, so a message is rejected after detection ran.
/// </summary>
/// <param name="inner">The codec that parses and rebuilds the message.</param>
internal sealed class RebuildRejectingCodec(IFeedCodec inner) : IFeedCodec
{

    #region Fields and Constants

    /// <summary>
    /// The value-free reason the rebuild check gives.
    /// </summary>
    public const string Reason = "Fixture rebuild check failed.";

    #endregion

    #region Methods

    /// <summary>
    /// Parses the message with the wrapped codec.
    /// </summary>
    /// <param name="message">The complete input delivery and its retained message bytes.</param>
    /// <returns>The wrapped codec's parse.</returns>
    public ParsedFeed Parse(FeedMessage message) => inner.Parse(message);

    /// <summary>
    /// Rebuilds the message with the wrapped codec.
    /// </summary>
    /// <param name="original">The original parse.</param>
    /// <param name="anonymizedFields">The protected values by field identifier.</param>
    /// <returns>The rebuilt message.</returns>
    public byte[] Serialize(ParsedFeed original, IReadOnlyDictionary<string, string> anonymizedFields) => inner.Serialize(original, anonymizedFields);

    /// <summary>
    /// Always rejects the rebuilt message.
    /// </summary>
    /// <param name="original">The original parse.</param>
    /// <param name="serialized">The rebuilt message.</param>
    /// <exception cref="InvalidDataException">Always.</exception>
    public void Validate(ParsedFeed original, ReadOnlyMemory<byte> serialized) => throw new InvalidDataException(Reason);

    #endregion
}
