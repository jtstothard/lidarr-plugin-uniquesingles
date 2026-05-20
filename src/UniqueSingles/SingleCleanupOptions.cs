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

    public SingleCleanupOptions(
        int durationToleranceMs,
        HashSet<string> comparisonReleaseTypes,
        Tier3Action tier3Action)
    {
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