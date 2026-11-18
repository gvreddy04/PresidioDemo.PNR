using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

/// <summary>
/// Checks the shipped recognizer file (WR-014): it loads, defines passenger-name (PERSON) recognizers only, and every
/// PERSON value of the frozen baseline has a custom recognizer in its feed's file. With <c>Presidio:DefaultDetection</c>
/// off (the shipped default) Presidio's own PERSON guesses do not run, so only a recognizer the file defines counts.
/// Every other baseline value is out of scope (names-only).
/// </summary>
public static class ConfigurationChecks
{

    #region Methods

    /// <summary>
    /// Runs the checks.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The number of checks that passed.</returns>
    public static int Run(string root)
    {
        const FeedType Feed = FeedType.PNR;
        string folder = FeedCatalog.Get(Feed).FolderName;
        string directory = Path.Combine(root, "src", "PresidioDemo.Core", "Configuration", "recognizers");
        var files = RecognizerFileReader.ReadAll(directory);
        int passed = 0;
        Check(files.Count == FeedCatalog.All.Count && files.ContainsKey(Feed), $"the {folder} recognizer file loads", ref passed);
        var file = files[Feed];
        Check(file.Recognizers.Count > 0 && file.Recognizers.All(r => r.Entity == ParityBaseline.NameEntity),
            $"the {folder} file defines passenger-name ({ParityBaseline.NameEntity}) recognizers only: " + string.Join(", ", file.Recognizers.Select(r => r.Name)), ref passed);
        Check(file.Anonymizers.ContainsKey(ParityBaseline.NameEntity) && file.Anonymizers.ContainsKey(FeedAnonymizer.DefaultEntity),
            "the shipped file sets PERSON and DEFAULT anonymizers", ref passed);
        var baseline = ParityBaseline.Load(root);
        int names = baseline.Protected.Count(ParityBaseline.InScope);
        var uncovered = UncoveredNames(baseline, files);
        Check(uncovered.Count == 0, $"every PERSON baseline value has a recognizer ({names} names in scope, "
            + $"{baseline.Protected.Count - names} other baseline values out of scope, names-only)"
            + (uncovered.Count == 0 ? "" : ": missing " + string.Join(", ", uncovered)), ref passed);
        // A made-up baseline with one name, so the check is shown to fail in every feed, including one without names.
        var oneName = baseline with { Protected = [new ParityEntry("made-up.bin", folder, "0", 4, ParityBaseline.NameEntity)] };
        var withoutNames = new Dictionary<FeedType, RecognizerFile>(files)
        {
            [Feed] = file with { Recognizers = file.Recognizers.Where(r => r.Entity != ParityBaseline.NameEntity).ToArray() },
        };
        Check(UncoveredNames(oneName, files).Count == 0 && UncoveredNames(oneName, withoutNames).SequenceEqual([folder + " " + ParityBaseline.NameEntity]),
            "the coverage check reports a feed with a baseline name but no name recognizer", ref passed);
        return passed;
    }

    /// <summary>
    /// Returns the feeds whose baseline has a PERSON value but whose file defines no PERSON recognizer. Presidio's
    /// predefined PERSON does not count: it does not run with <c>Presidio:DefaultDetection</c> off.
    /// </summary>
    /// <param name="baseline">The frozen baseline.</param>
    /// <param name="files">The recognizer files by feed.</param>
    /// <returns>"FEED PERSON" entries in ordinal order.</returns>
    public static IReadOnlyList<string> UncoveredNames(ParityBaselineDocument baseline, IReadOnlyDictionary<FeedType, RecognizerFile> files) =>
        baseline.Protected
            .Where(ParityBaseline.InScope)
            .Select(e => (Type: FeedCatalog.All.Single(f => f.FolderName == e.Feed).Type, Name: e.Feed, e.Entity))
            .Distinct()
            .Where(e => !files[e.Type].Recognizers.Any(r => r.Entity == e.Entity))
            .Select(e => e.Name + " " + e.Entity)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Throws when a condition fails.
    /// </summary>
    private static void Check(bool condition, string label, ref int passed)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
        Console.WriteLine("PASS " + label);
        passed++;
    }

    #endregion
}
