using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PresidioDemo.Core.Decoder;
using PresidioDemo.Core.Logging;
using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

#region Models

/// <summary>
/// One approved parity exception: a baseline-protected value that decoded-text detection publishes (W-011, W-016).
/// </summary>
/// <param name="Sample">The sample file name.</param>
/// <param name="Location">The baseline location.</param>
/// <param name="Reason">Why it is published: a keep-format match or a binary string shorter than four characters.</param>
/// <param name="ApprovedBy">Who approved it, when recorded; optional (WR-019).</param>
public sealed record ParityException(string Sample, string Location, string Reason, string? ApprovedBy = null);

/// <summary>
/// A value-free finding: where, what entity, which recognizer decided, and the character shape.
/// </summary>
/// <param name="Sample">The sample file name.</param>
/// <param name="Feed">The feed folder name.</param>
/// <param name="Location">The payload offset.</param>
/// <param name="Length">The value length.</param>
/// <param name="Entity">The baseline entity.</param>
/// <param name="Shape">The character shape of the value.</param>
/// <param name="Decision">The decoded-text decision for the containing string.</param>
/// <param name="Format">The recognizer names that decided, or empty.</param>
public sealed record SpikeFinding(string Sample, string Feed, string Location, int Length, string Entity, string Shape, string Decision, string Format);

#endregion

/// <summary>
/// The decoded-text spike (V-01, V-02, V-11): parity against the value-free baseline (WR-019), rebuilds, negative samples
/// and throughput, with the shipped <c>Presidio:DefaultDetection</c> off. Only the baseline values whose entity a recognizer
/// defined in the feed's file produces are checked (<see cref="ParityBaseline.InScope"/>); every other value is counted as
/// out of scope and never fails the run. Extra masking outside the baseline never fails it either.
/// </summary>
public static class Spike
{

    #region Methods

    /// <summary>
    /// Runs the spike and writes <c>report.json</c> (values, Local only) and <c>summary.json</c> (value-free).
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="output">The run folder under <c>.test-runs</c>.</param>
    /// <param name="rounds">How many rounds over the production samples the throughput run makes.</param>
    /// <returns>True when every baseline value in scope is protected or an approved exception, every approved exception is
    /// still needed, and every sample rebuilds (WR-019).</returns>
    public static async Task<bool> RunAsync(string root, string output, int rounds)
    {
        var baseline = ParityBaseline.Load(root);
        string exceptionsFile = Path.Combine(root, "tests", "fixtures", "parity-exceptions.json");
        var exceptions = File.Exists(exceptionsFile)
            ? JsonSerializer.Deserialize<List<ParityException>>(File.ReadAllText(exceptionsFile), ParityBaseline.JsonOptions) ?? []
            : [];
        var protector = DetectionRun.CreateProtector(root);
        var files = ParityBaseline.Recognizers(root);

        var results = new List<SampleResult>();
        foreach (string path in SampleFiles.All(root))
        {
            results.Add(await DetectionRun.ProcessAsync(protector, path, CancellationToken.None));
        }
        var byName = results.ToDictionary(r => Path.GetFileName(r.Path), StringComparer.Ordinal);

        var caught = new List<ParityEntry>();
        var misses = new List<SpikeFinding>();
        var missValues = new List<object>();
        int outOfScope = 0;
        foreach (var entry in baseline.Protected)
        {
            if (!ParityBaseline.InScope(entry, files))
            {
                // No recognizer the file defines produces this entity, so the value is reported, never failed.
                outOfScope++;
                continue;
            }
            var result = byName[entry.Sample];
            var (isCaught, before, after, evt) = Inspect(result, entry.Location, entry.Length);
            if (isCaught)
            {
                caught.Add(entry);
                continue;
            }
            misses.Add(new SpikeFinding(entry.Sample, entry.Feed, entry.Location, entry.Length, entry.Entity, DetectingFeedProtector.Shape(before),
                evt is null ? "rejected" : DetectionLog.Decision(evt.Decision), evt?.Recognizer ?? ""));
            missValues.Add(new { entry.Sample, entry.Location, entry.Entity, Original = before, Published = after });
        }
        var unapproved = misses.Where(m => !exceptions.Any(e => e.Sample == m.Sample && e.Location == m.Location)).ToArray();
        // An exception for a value that is now caught, or that is out of scope, must be removed rather than kept.
        var stale = exceptions.Where(e => !misses.Any(m => m.Sample == e.Sample && m.Location == e.Location)).ToArray();

        var production = results.Where(r => !Path.GetFileName(r.Path).Contains("Negative_Testing", StringComparison.Ordinal)).ToArray();
        int rebuilt = production.Count(r => r.Output is not null);

        // spaCy runs with default detection off only for a custom recognizer with context words (see presidio_demo.py).
        bool contextWords = files.Values.Any(file => file.Recognizers.Any(r => r.Context.Count > 0));
        var throughput = await Throughput(protector, production.Select(r => r.Path).ToArray(), rounds, contextWords);
        // WR-019: a baseline value in scope published unchanged fails until it is protected or approved; extra masking
        // never fails.
        bool parity = unapproved.Length == 0 && stale.Length == 0 && rebuilt == production.Length;
        var negatives = results.Where(r => Path.GetFileName(r.Path).Contains("Negative_Testing", StringComparison.Ordinal))
            .Select(r => new { Sample = Path.GetFileName(r.Path), Status = r.Output is null ? "rejected" : "published", Reason = r.Reason ?? "" }).ToArray();

        var summary = new
        {
            Parity = parity ? "PASS" : "FAIL",
            Scope = "baseline values whose entity a recognizer defined in the feed's file produces: "
                + string.Join(", ", files.Values.SelectMany(file => file.DefinedEntities).Distinct().Order(StringComparer.Ordinal)),
            DefaultDetection = protector.DefaultDetection,
            Protected = new
            {
                Baseline = baseline.Protected.Count,
                InScope = caught.Count + misses.Count,
                OutOfScope = outOfScope,
                Caught = caught.Count,
                Missed = misses.Count,
                ApprovedExceptions = misses.Count - unapproved.Length,
                Unapproved = unapproved.Length,
                StaleExceptions = stale.Length
            },
            Rebuilds = new { Production = production.Length, Valid = rebuilt },
            Negatives = negatives,
            Misses = misses,
            Throughput = throughput,
            Rejected = results.Where(r => r.Output is null).Select(r => new { Sample = Path.GetFileName(r.Path), Reason = r.Reason ?? "" })
        };
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, ParityBaseline.JsonOptions) + Environment.NewLine);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            Note = "Local spike report: contains original sample values. Never commit or copy outside .test-runs.",
            Misses = missValues
        }, ParityBaseline.JsonOptions) + Environment.NewLine);
        Console.WriteLine($"SPIKE parity={summary.Parity} | default detection {(protector.DefaultDetection ? "on" : "off")} | " +
            $"{caught.Count + misses.Count} values in scope: {caught.Count} caught, {misses.Count} missed ({misses.Count - unapproved.Length} approved, " +
            $"{unapproved.Length} unapproved, {stale.Length} stale exceptions) | {outOfScope} values out of scope | rebuilds {rebuilt}/{production.Length}");
        Console.WriteLine("SPIKE throughput " + JsonSerializer.Serialize(throughput));
        Console.WriteLine("SPIKE summary " + Path.Combine(output, "summary.json"));
        return parity;
    }

    /// <summary>
    /// Returns whether a baseline location is protected in the decoded-text output, with the before and after text.
    /// </summary>
    /// <param name="result">The sample result.</param>
    /// <param name="location">The payload offset.</param>
    /// <param name="length">The value length.</param>
    /// <returns>Caught when every non-<c>*</c> character changed.</returns>
    private static (bool Caught, string Before, string After, DetectionEvent? Event) Inspect(SampleResult result, string location, int length)
    {
        string before = Value(result.Original, result, location, length, original: true);
        if (result.Output is null)
        {
            return (true, before, "", null);
        }
        string after = Value(result.Output, result, location, length, original: false);
        bool caught = before.Zip(after).All(pair => pair.First == '*' || pair.First != pair.Second);
        return (caught, before, after, Containing(result, location, length));
    }

    /// <summary>
    /// Returns the payload bytes at a location in a message.
    /// </summary>
    private static string Value(byte[] message, SampleResult result, string location, int length, bool original)
    {
        byte[] payload = FeedPayloadReader.ReadPayload(new FeedMessage(new FeedDelivery(result.Feed, Guid.NewGuid(), Path.GetFileName(result.Path), result.Path), message));
        return Encoding.ASCII.GetString(payload, int.Parse(location, System.Globalization.CultureInfo.InvariantCulture), length);
    }

    /// <summary>
    /// Returns the event of the decoded string that contains a location.
    /// </summary>
    private static DetectionEvent? Containing(SampleResult result, string location, int length)
    {
        if (result.Outcome is null || result.Parsed is null)
        {
            return null;
        }
        int offset = int.Parse(location, System.Globalization.CultureInfo.InvariantCulture);
        var candidate = XdrFeedDecoder.Candidates(result.Parsed).FirstOrDefault(c => offset >= c.Offset && offset + length <= c.Offset + c.Length);
        return candidate is null ? null : result.Outcome.Events.FirstOrDefault(e => e.Slot == candidate.Slot);
    }

    /// <summary>
    /// Measures messages per second for one process (one ECS task) in the protector's detection mode, and the task count
    /// that sustains the feed's sizing peak of 400 messages per second (its share of the 2,000 messages per second sized
    /// for the airline feeds before each feed got its own worker).
    /// </summary>
    private static async Task<object> Throughput(DetectingFeedProtector protector, IReadOnlyList<string> samples, int rounds, bool contextWords)
    {
        foreach (string path in samples)
        {
            await DetectionRun.ProcessAsync(protector, path, CancellationToken.None);
        }
        var perFeed = new Dictionary<string, (int Messages, double Seconds)>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();
        int messages = 0;
        for (int round = 0; round < rounds; round++)
        {
            foreach (string path in samples)
            {
                var timer = Stopwatch.StartNew();
                var result = await DetectionRun.ProcessAsync(protector, path, CancellationToken.None);
                string feed = FeedCatalog.Get(result.Feed).FolderName;
                perFeed[feed] = perFeed.TryGetValue(feed, out var current) ? (current.Messages + 1, current.Seconds + timer.Elapsed.TotalSeconds) : (1, timer.Elapsed.TotalSeconds);
                messages++;
            }
        }
        total.Stop();
        const double peakPerFeed = 400;
        var rows = perFeed.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            double rate = p.Value.Messages / p.Value.Seconds;
            return new { Feed = p.Key, MessagesPerSecondPerTask = Math.Round(rate, 1), Tasks = (int)Math.Ceiling(peakPerFeed / rate) };
        }).ToArray();
        return new
        {
            Rounds = rounds,
            Messages = messages,
            // With default detection off no recognizer reads spaCy's named entities: spaCy runs without its NER component
            // when a custom recognizer has context words, and not at all otherwise.
            NlpNer = protector.DefaultDetection ? "on (spaCy en_core_web_lg)"
                : contextWords ? "off (Presidio:DefaultDetection false); spaCy en_core_web_lg runs without NER for context words"
                : "off (Presidio:DefaultDetection false); spaCy does not run (no context words)",
            MessagesPerSecondPerTask = Math.Round(messages / total.Elapsed.TotalSeconds, 1),
            Feeds = rows,
            TasksForPeak = rows.Sum(r => r.Tasks),
            Assumption = $"400 msg/s peak for the {string.Join(", ", FeedCatalog.All.Select(f => f.FolderName))} feed; one worker process per ECS task"
        };
    }

    #endregion
}
