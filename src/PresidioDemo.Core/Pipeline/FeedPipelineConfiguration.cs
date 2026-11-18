using PresidioDemo.Core.Models;
using PresidioDemo.Core.Protection;

namespace PresidioDemo.Core.Pipeline;

/// <summary>
/// The validated per-feed recognizer files that drive every feed workflow (WR-014).
/// </summary>
/// <param name="Recognizers">The recognizer file of each feed.</param>
/// <param name="DefaultDetection">Whether Presidio's predefined recognizers run beside the defined ones
/// (<c>Presidio:DefaultDetection</c>); off by default, so only the recognizers each file defines act.</param>
public sealed record FeedPipelineConfiguration(IReadOnlyDictionary<FeedType, RecognizerFile> Recognizers, bool DefaultDetection = false)
{

    #region Methods

    /// <summary>
    /// Loads and validates one recognizer XML file per feed before any input is read (WR-014).
    /// </summary>
    /// <param name="recognizerDirectory">The directory holding <c>pnr.xml</c>.</param>
    /// <param name="defaultDetection">Whether Presidio's predefined recognizers run too (<c>Presidio:DefaultDetection</c>).</param>
    /// <returns>The validated configuration.</returns>
    /// <exception cref="InvalidDataException">A file is missing or invalid.</exception>
    public static FeedPipelineConfiguration Load(string recognizerDirectory, bool defaultDetection = false) =>
        new(RecognizerFileReader.ReadAll(recognizerDirectory), defaultDetection);

    /// <summary>
    /// Creates the detecting protector for these recognizer files. Call <see cref="DetectingFeedProtector.Configure"/>
    /// after <see cref="PythonHost.Start"/>.
    /// </summary>
    /// <returns>The protector, with or without Presidio's predefined recognizers as <see cref="DefaultDetection"/> says.</returns>
    public DetectingFeedProtector CreateProtector() => new(Recognizers, DefaultDetection);

    /// <summary>
    /// Creates the workflow registry using the given protector.
    /// </summary>
    /// <param name="protector">The local detection and protection service.</param>
    /// <returns>One workflow per supported feed.</returns>
    public FeedWorkflowRouter CreateRouter(IFeedProtector protector) => FeedWorkflowRouter.Create(protector, FeedCodecRegistry.Create);

    #endregion
}
