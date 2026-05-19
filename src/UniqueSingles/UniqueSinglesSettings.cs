using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;

namespace UniqueSingles;

public enum Tier3Action
{
    FlagOnly = 0,
    Skip = 1
}

/// <summary>
/// Lidarr provider settings for the Unique Singles connection.
/// Defaults are intentionally conservative: only singles are checked and title-only matches are never deleted.
/// </summary>
public class UniqueSinglesSettings : IProviderConfig
{
    public UniqueSinglesSettings()
    {
        DurationToleranceMs = 3000;
        ReleaseTypesToCheck = "Single";
        Tier3Action = Tier3Action.FlagOnly;
    }

    [FieldDefinition(Order = 0, Label = "Duration tolerance (ms)", Type = FieldType.Number, HelpText = "Maximum duration difference for title + duration matching. Defaults to 3000 ms.")]
    public int DurationToleranceMs { get; set; }

    [FieldDefinition(Order = 1, Label = "Release types to check", Type = FieldType.Textbox, HelpText = "Comma-separated album types that should be checked as singles. Defaults to Single.")]
    public string ReleaseTypesToCheck { get; set; }

    [FieldDefinition(Order = 2, Label = "Title-only match action", Type = FieldType.Select, HelpText = "Safe behavior for Tier 3 title-only matches. Defaults to flag-only and never deletes.")]
    public Tier3Action Tier3Action { get; set; }

    /// <summary>
    /// Converts settings to a validated SingleCleanupOptions snapshot.
    /// The ReleaseTypesToCheck field specifies which album types are used for comparison tracks.
    /// Malformed or empty values fall back to safe defaults (Album, EP).
    /// </summary>
    public SingleCleanupOptions ToCleanupOptions()
    {
        var releaseTypes = ParseReleaseTypes(ReleaseTypesToCheck);
        return new SingleCleanupOptions(DurationToleranceMs, releaseTypes, Tier3Action);
    }

    private static HashSet<string> ParseReleaseTypes(string? releaseTypesString)
    {
        if (string.IsNullOrWhiteSpace(releaseTypesString))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };
        }

        var types = releaseTypesString.Split(',')
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => char.ToUpper(t[0]) + t.Substring(1).ToLower())
            .ToList();

        return types.Count > 0
            ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" };
    }
}
