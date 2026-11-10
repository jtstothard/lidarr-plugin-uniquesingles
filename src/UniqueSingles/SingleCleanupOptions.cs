using System;

using System.Collections.Generic;
namespace NzbDrone.Core.Plugins;

/// <summary>
/// A validated, type-safe snapshot of Unique Singles settings used during cleanup operations.
/// Clamps invalid values to safe defaults so cleanup never fails due to bad settings.
/// </summary>
public class SingleCleanupOptions
{
    /// <summary>
    /// Gets whether verified whole-release exact-audio coverage may authorize cleanup.
    /// Defaults to false; report-only analysis grants no additional cleanup authority.
    /// </summary>
    public bool ExactAudioCleanup { get; }
    public bool ExactAudioAnalysis { get; }
    public string ExactAudioDecoderPath { get; }
    public System.Threading.CancellationToken AudioCancellation { get; }
    public AudioAnalysisBudget AudioBudget { get; }

    /// <summary>
    /// Gets the duration tolerance in milliseconds for Tier 2 matching.
    /// Clamped to minimum 1000 ms for safety.
    /// </summary>
    public int DurationToleranceMs { get; }

    /// <summary>
    /// Gets the album/EP types to use for comparison tracks.
    /// Always contains at least "Album" and "EP" as safe defaults.
    /// </summary>
    public HashSet<string> ComparisonReleaseTypes { get; }

    /// <summary>
    /// Gets the action to take for Tier 3 (title-only) matches.
    /// Defaults to FlagOnly for safety.
    /// </summary>
    public Tier3Action Tier3Action { get; }

    /// <summary>
    /// Gets whether monitored singles with no downloaded files may be eagerly unmonitored
    /// as soon as every catalog track is Tier-1/Tier-2 redundant (#26). Purely preventive:
    /// the eager path never deletes files and never touches releases with downloaded
    /// content. Defaults to true (the behavior issue #26 asks for).
    /// </summary>
    public bool EagerUnmonitorRedundantSingles { get; }

    public SingleCleanupOptions(
        int durationToleranceMs,
        HashSet<string> comparisonReleaseTypes,
        Tier3Action tier3Action,
        bool exactAudioAnalysis = false,
        string exactAudioDecoderPath = "ffmpeg",
        System.Threading.CancellationToken audioCancellation = default,
        AudioAnalysisBudget? audioBudget = null,
        bool exactAudioCleanup = false,
        bool eagerUnmonitorRedundantSingles = true)
    {
        ExactAudioCleanup = exactAudioCleanup;
        EagerUnmonitorRedundantSingles = eagerUnmonitorRedundantSingles;
        ExactAudioAnalysis = exactAudioAnalysis || exactAudioCleanup;
        ExactAudioDecoderPath = string.IsNullOrWhiteSpace(exactAudioDecoderPath) ? "ffmpeg" : exactAudioDecoderPath;
        AudioCancellation = audioCancellation;
        AudioBudget = audioBudget ?? new AudioAnalysisBudget();
        // Clamp duration tolerance to a safe minimum (1 second)
        DurationToleranceMs = durationToleranceMs <= 0 ? 3000 : Math.Max(durationToleranceMs, 1000);

        // Ensure comparison release types is non-empty
        ComparisonReleaseTypes = comparisonReleaseTypes?.Count > 0
            ? comparisonReleaseTypes
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };

        // Ensure Tier 3 action is valid
        Tier3Action = Enum.IsDefined(typeof(Tier3Action), tier3Action)
            ? tier3Action
            : Tier3Action.FlagOnly;
    }

    /// <summary>
    /// Returns a copy of this snapshot with a different audio-analysis cancellation token,
    /// preserving every other setting (including the settings precedence contract). Entry
    /// points use this to link dispatcher cancellation into the resolved options.
    /// </summary>
    public SingleCleanupOptions WithAudioCancellation(System.Threading.CancellationToken audioCancellation)
    {
        return new SingleCleanupOptions(
            DurationToleranceMs,
            ComparisonReleaseTypes,
            Tier3Action,
            ExactAudioAnalysis,
            ExactAudioDecoderPath,
            audioCancellation,
            AudioBudget,
            ExactAudioCleanup,
            EagerUnmonitorRedundantSingles);
    }

    /// <summary>
    /// Determines whether an album should be used as a comparison track source
    /// based on the configured comparison release types.
    /// </summary>
    public bool ShouldCompareAgainstType(string albumType)
    {
        if (string.IsNullOrWhiteSpace(albumType))
        {
            return false;
        }

        return ComparisonReleaseTypes.Contains(albumType);
    }
}