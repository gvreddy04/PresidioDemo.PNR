using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.DetectionParity;

/// <summary>
/// Checks the Presidio-style recognizer file reader (WR-014): a valid file loads with its switches, custom recognizers,
/// allow list and anonymizers, and every invalid or retired form stops startup with a value-free message naming it.
/// All values are made up.
/// </summary>
public static class RecognizerFileChecks
{

    #region Fields and Constants

    /// <summary>
    /// A small valid file that uses every element.
    /// </summary>
    private const string Valid = """
        <?xml version="1.0" encoding="utf-8"?>
        <recognizers feed="PNR" language="en" default_score_threshold="0.6">
          <recognizer name="CryptoRecognizer" type="predefined" enabled="false" />
          <recognizer name="passengerName" type="custom" supported_entity="PERSON">
            <pattern name="surname/given" score="0.9">(?&lt;![A-Z])[A-Z]{2,}/[A-Z]+</pattern>
            <context>DOCS NAME</context>
          </recognizer>
          <recognizer name="titles" type="custom" supported_entity="TITLE" deny_list_score="0.8">
            <deny_list>MR MRS DR</deny_list>
          </recognizer>
          <recognizer name="unused" type="custom" supported_entity="ORDER_ID" enabled="false">
            <pattern name="order" score="0.85">ORD\d+</pattern>
          </recognizer>
          <allow_list match="regex">
            <item>\d{4}</item>
            <item>FQT[RS]</item>
          </allow_list>
          <anonymizer entity="PERSON" type="mask" masking_char="*" />
          <anonymizer entity="TITLE" type="replace" new_value="[T]" />
          <anonymizer entity="DEFAULT" type="mask" masking_char="#" />
        </recognizers>
        """;

    #endregion

    #region Methods

    /// <summary>
    /// Runs the checks.
    /// </summary>
    /// <returns>The number of checks that passed.</returns>
    public static int Run()
    {
        int passed = 0;
        string scratch = Path.Combine(Path.GetTempPath(), "recognizer-file-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var file = Load(scratch, Valid);
            Check(file.Language == "en" && file.Threshold == 0.6, "root attributes are read", ref passed);
            Check(file.Disabled.SequenceEqual(["CryptoRecognizer"]), "a predefined recognizer with enabled=\"false\" is switched off", ref passed);
            Check(file.Recognizers.Select(r => r.Name).SequenceEqual(["passengerName", "titles"]), "custom recognizers load; a disabled one is left out", ref passed);
            var name = file.Recognizers[0];
            Check(name.Entity == "PERSON" && name.Patterns.Single() is { Name: "surname/given", Score: 0.9 } && name.Context.SequenceEqual(["DOCS", "NAME"]),
                "a pattern keeps its name and score, context words are split on spaces", ref passed);
            Check(file.Recognizers[1].DenyList.SequenceEqual(["MR", "MRS", "DR"]) && file.Recognizers[1].DenyListScore == 0.8, "a deny list and its score load", ref passed);
            Check(file.AllowList.MatchesWhole("1405") && file.AllowList.MatchesWhole("fqtr") && !file.AllowList.MatchesWhole("XXXX1111")
                && !file.AllowList.MatchesWhole("APPR 1234 OK"), "allow-list items match whole strings only", ref passed);
            Check(file.ActionFor("PERSON") is ({ Operator: AnonymizerOperator.Mask, MaskingChar: '*' }, true)
                && file.ActionFor("CREDIT_CARD") is ({ Operator: AnonymizerOperator.Mask, MaskingChar: '#' }, false),
                "an entity uses its own anonymizer, else DEFAULT", ref passed);
            var bare = Load(scratch, "<recognizers feed=\"PNR\" />");
            Check(bare.Disabled.Count == 0 && bare.Recognizers.Count == 0 && bare.Threshold == 0.5 && !bare.AllowList.MatchesWhole("1405")
                && bare.ActionFor("PERSON") is ({ Operator: AnonymizerOperator.Replace }, false), "an empty file loads with Presidio's defaults and replace", ref passed);
            Check(Load(scratch, "<recognizers feed=\"PNR\"><allow_list><item>FQTR</item></allow_list></recognizers>").AllowList is { Regex: false } exact
                && exact.MatchesWhole("FQTR") && !exact.MatchesWhole("fqtr"), "an exact allow list compares the whole text exactly", ref passed);
            Check(file.ToBridgeJson(false).Contains("\"disabled\":[\"CryptoRecognizer\"]", StringComparison.Ordinal)
                && file.ToBridgeJson(false).Contains("\"denyList\":[\"MR\",\"MRS\",\"DR\"]", StringComparison.Ordinal), "the bridge JSON carries switches and deny lists", ref passed);
            Check(file.ToBridgeJson(false).Contains("\"defaultDetection\":false", StringComparison.Ordinal)
                && file.ToBridgeJson(true).Contains("\"defaultDetection\":true", StringComparison.Ordinal),
                "the bridge JSON carries Presidio:DefaultDetection", ref passed);

            // Lone-name recognizers (type="lone_name"), declared before or after their source.
            const string LoneName = "<recognizer name=\"lonePart\" type=\"lone_name\" source=\"passengerName\" supported_entity=\"PERSON\" />";
            string WithLone(string element) => Valid.Replace("</recognizers>", element + "</recognizers>");
            var withLone = Load(scratch, WithLone(LoneName));
            Check(withLone.LoneNames is [{ Name: "lonePart", Entity: "PERSON", Source: "passengerName" }] && withLone.Recognizers.Count == 2
                && !withLone.ToBridgeJson(false).Contains("lonePart", StringComparison.Ordinal),
                "a lone_name recognizer loads with its source and entity, and is not sent to Presidio", ref passed);
            Check(withLone.DefinedEntities.SetEquals(["PERSON", "TITLE"]) && file.DefinedEntities.SetEquals(["PERSON", "TITLE"])
                && Load(scratch, WithLone(LoneName.Replace("\"PERSON\"", "\"PASSENGER\""))).DefinedEntities.Contains("PASSENGER"),
                "the defined entities are those of the custom and lone-name recognizers", ref passed);
            Check(Load(scratch, Valid.Replace("<recognizer name=\"passengerName\"", LoneName + "<recognizer name=\"passengerName\"")).LoneNames?.Count == 1
                && Load(scratch, WithLone(LoneName.Replace(" />", " enabled=\"false\" />"))).LoneNames?.Count == 0,
                "a lone_name recognizer may come before its source, and enabled=\"false\" leaves it out", ref passed);
            Check(LoneNameRecognizer.Parts("MILLER-SMITH/ANN MR").SequenceEqual(["MILLER", "SMITH", "ANN"])
                && LoneNameRecognizer.Parts("O'NEIL/J DR").SequenceEqual(["NEIL"]) && !LoneNameRecognizer.Parts("MISS/MSTR").Any(),
                "name parts split on / space - and ', keep two or more letters and drop titles", ref passed);
            Reject(scratch, WithLone(LoneName.Replace("source=\"passengerName\"", "source=\"noSuchRecognizer\"")), "a lone_name with an unknown source", ref passed, "not a custom recognizer");
            Reject(scratch, WithLone(LoneName.Replace("source=\"passengerName\"", "source=\"unused\"")), "a lone_name whose source is switched off", ref passed, "enabled");
            Reject(scratch, WithLone(LoneName.Replace("source=\"passengerName\"", "source=\"CryptoRecognizer\"")), "a lone_name whose source is predefined", ref passed, "not a custom recognizer");
            Reject(scratch, WithLone(LoneName.Replace("source=\"passengerName\"", "source=\"lonePart\"")), "a lone_name that is its own source", ref passed, "own source");
            Reject(scratch, WithLone(LoneName.Replace(" source=\"passengerName\"", "")), "a lone_name without a source", ref passed, "needs a source");
            Reject(scratch, WithLone(LoneName.Replace("\"PERSON\"", "\"person\"")), "a lone_name with a lower-case entity", ref passed);
            Reject(scratch, WithLone(LoneName.Replace(" />", " score=\"0.9\" />")), "a lone_name with an unknown attribute", ref passed, "unknown attribute");
            Reject(scratch, WithLone(LoneName.Replace(" />", "><pattern name=\"x\">X</pattern></recognizer>")), "a lone_name with a child element", ref passed, "no child elements");

            Reject(scratch, "<recognizers feed=\"PNR\"", "not well-formed XML", ref passed);
            Reject(scratch, "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x \"y\">]><recognizers feed=\"PNR\" />", "a DTD", ref passed);
            Reject(scratch, Valid.Replace("feed=\"PNR\"", "feed=\"OTHER\""), "the wrong feed attribute", ref passed);
            Reject(scratch, Valid.Replace("language=\"en\"", "language=\"de\""), "a language other than en", ref passed);
            Reject(scratch, Valid.Replace("0.6", "1.5"), "a threshold above 1", ref passed);
            Reject(scratch, Valid.Replace("ORD\\d+", "(unclosed"), "a pattern that does not compile", ref passed);
            Reject(scratch, Valid.Replace("\\d{4}", "(unclosed"), "an allow-list item that does not compile", ref passed);
            // An unbalanced ")" is invalid alone but valid inside the whole-string wrapper, where it would match every string.
            Reject(scratch, Valid.Replace("<item>FQT[RS]</item>", "<item>FQTR)|(?:.*</item>"), "an allow-list item that escapes its anchor", ref passed, "does not compile");
            Reject(scratch, Valid.Replace("ORD\\d+", "ORD)|(?:.*"), "a pattern that escapes its anchor", ref passed, "does not compile");
            Reject(scratch, Valid.Replace("name=\"titles\"", "name=\"passengerName\""), "a duplicate recognizer name", ref passed);
            Reject(scratch, Valid.Replace("type=\"predefined\"", "type=\"builtin\""), "an unknown recognizer type", ref passed);
            Reject(scratch, Valid.Replace("enabled=\"false\" />", "enabled=\"no\" />"), "an enabled value other than true or false", ref passed);
            Reject(scratch, Valid.Replace("supported_entity=\"PERSON\"", "supported_entity=\"person\""), "a lower-case entity", ref passed);
            Reject(scratch, Valid.Replace("<deny_list>MR MRS DR</deny_list>", ""), "a custom recognizer without a pattern or deny list", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"encrypt\" key=\"k\""), "the encrypt operator", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"custom\""), "the custom operator", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"surrogate_ahds\""), "the surrogate_ahds operator", ref passed);
            Reject(scratch, Valid.Replace("type=\"replace\" new_value=\"[T]\"", "type=\"scramble\""), "an unknown operator", ref passed);
            Reject(scratch, Valid.Replace("entity=\"TITLE\"", "entity=\"PERSON\""), "two anonymizers for one entity", ref passed);
            Reject(scratch, Valid.Replace("<allow_list match=\"regex\">", "<allow_list match=\"glob\">"), "an unknown allow-list match", ref passed);
            Reject(scratch, Valid.Replace("</recognizers>", "<allow_list><item>X</item></allow_list></recognizers>"), "a second allow list", ref passed);
            Reject(scratch, Valid.Replace("<context>DOCS NAME</context>", "<context>DOCS</context><rule />"), "an unknown child element", ref passed);
            Reject(scratch, Valid.Replace("</recognizers>", "<rule /></recognizers>"), "an unknown element", ref passed);
            Reject(scratch, Valid.Replace("type=\"predefined\"", "type=\"predefined\" colour=\"red\""), "an unknown attribute", ref passed);
            // Elements and attributes of the earlier format fail loudly instead of being ignored.
            Reject(scratch, Valid.Replace("</recognizers>", "<builtIn entity=\"PERSON\" /></recognizers>"), "a retired <builtIn>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("</recognizers>", "<keep name=\"time\"><pattern>\\d{4}</pattern></keep></recognizers>"), "a retired <keep>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("</recognizers>", "<format name=\"x\" entity=\"X\"><pattern>X</pattern></format></recognizers>"), "a retired <format>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("</recognizers>", "<pnrAnchor after=\"passengerName\" /></recognizers>"), "a retired <pnrAnchor>", ref passed, "no longer supported");
            Reject(scratch, Valid.Replace("language=\"en\"", "language=\"en\" defaultMaskCharacter=\"#\""), "a retired defaultMaskCharacter", ref passed, "no longer supported");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
        return passed;
    }

    /// <summary>
    /// Writes the content as <c>pnr.xml</c> and reads it.
    /// </summary>
    private static RecognizerFile Load(string scratch, string content)
    {
        string path = Path.Combine(scratch, "pnr.xml");
        File.WriteAllText(path, content);
        return RecognizerFileReader.Read(path, FeedType.PNR);
    }

    /// <summary>
    /// Requires the reader to reject the content with a message naming the file (and, when given, containing a phrase).
    /// </summary>
    private static void Reject(string scratch, string content, string label, ref int passed, string? phrase = null)
    {
        try
        {
            Load(scratch, content);
        }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("Recognizer file pnr.xml", StringComparison.Ordinal)
            && (phrase is null || ex.Message.Contains(phrase, StringComparison.Ordinal)))
        {
            Console.WriteLine("PASS rejected " + label + " (" + ex.Message + ")");
            passed++;
            return;
        }
        throw new Exception("Expected the reader to reject " + label);
    }

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
