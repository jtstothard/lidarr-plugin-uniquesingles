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
}
