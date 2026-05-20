using System;
using FluentValidation;
using System.Linq;
using System.Collections.Generic;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Plugins;

public enum Tier3Action
{
    [FieldOption(Label = "Flag for review", Hint = "Keep title-only matches for manual review. Never auto-delete.")]
    FlagOnly = 0,

    [FieldOption(Label = "Skip cleanup", Hint = "Ignore title-only matches and leave the single alone.")]
    Skip = 1,

    [FieldOption(Label = "Auto-clean", Hint = "Automatically clean title-only matches. Use with caution — this deletes singles that match by title alone.")]
    AutoClean = 2
}

/// <summary>
/// Lidarr provider settings for the Unique Singles connection.
/// Defaults are intentionally conservative: comparison tracks come from Album/EP releases,
/// and title-only matches are flagged for review rather than deleted.
/// </summary>
public class UniqueSinglesSettings : IProviderConfig
{
    private static readonly string[] SafeDefaultReleaseTypes = { "Album", "EP" };
    private const string DefaultReleaseTypesToCheck = "Album, EP";
    private static readonly UniqueSinglesSettingsValidator Validator = new UniqueSinglesSettingsValidator();

    public UniqueSinglesSettings()
    {
        DurationToleranceMs = 3000;
        ReleaseTypesToCheck = DefaultReleaseTypesToCheck;
        Tier3Action = Tier3Action.FlagOnly;
        ScanIntervalMinutes = 1440;
    }

    [FieldDefinition(0, Label = "Duration tolerance (ms)", Type = FieldType.Number, HelpText = "Maximum duration difference for title + duration matching. Defaults to 3000 ms.")]
    public int DurationToleranceMs { get; set; }

    [FieldDefinition(1, Label = "Release types to compare", Type = FieldType.Textbox, HelpText = "Comma-separated release types used as comparison tracks for imported singles. Defaults to Album, EP.")]
    public string ReleaseTypesToCheck { get; set; }

    [FieldDefinition(2, Label = "Title-only match action", Type = FieldType.Select, SelectOptions = typeof(Tier3Action), HelpText = "Safe behavior for Tier 3 title-only matches. Defaults to flag for review and never auto-deletes.")]
    public Tier3Action Tier3Action { get; set; }

    [FieldDefinition(3, Label = "Scan interval (minutes)", Type = FieldType.Number, HelpText = "How often to run automatic scan. Minimum 60 minutes. Defaults to 1440 (24 hours).")]
    public int ScanIntervalMinutes { get; set; }

    /// <summary>
    /// Converts settings to a validated SingleCleanupOptions snapshot.
    /// The ReleaseTypesToCheck field persists which release types are used for comparison tracks.
    /// Malformed or empty values fall back to the safe Album/EP baseline.
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
            return CreateSafeDefaultReleaseTypes();
        }

        var types = releaseTypesString.Split(',')
            .Select(t => NormalizeReleaseType(t.Trim()))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return types.Count > 0
            ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase)
            : CreateSafeDefaultReleaseTypes();
    }

    private static HashSet<string> CreateSafeDefaultReleaseTypes()
    {
        return new HashSet<string>(SafeDefaultReleaseTypes, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeReleaseType(string releaseType)
    {
        if (string.IsNullOrWhiteSpace(releaseType))
        {
            return string.Empty;
        }

        if (releaseType.Equals("EP", StringComparison.OrdinalIgnoreCase))
        {
            return "EP";
        }

        if (releaseType.Equals("Album", StringComparison.OrdinalIgnoreCase))
        {
            return "Album";
        }

        if (releaseType.Equals("Single", StringComparison.OrdinalIgnoreCase))
        {
            return "Single";
        }

        return char.ToUpperInvariant(releaseType[0]) + releaseType[1..].ToLowerInvariant();
    }

    public NzbDroneValidationResult Validate()
    {
        return new NzbDroneValidationResult(Validator.Validate(this));
    }
}

public class UniqueSinglesSettingsValidator : AbstractValidator<UniqueSinglesSettings>
{
    public UniqueSinglesSettingsValidator()
    {
        RuleFor(c => c.DurationToleranceMs).GreaterThan(0)
            .WithMessage("Duration tolerance must be greater than 0");
        RuleFor(c => c.ScanIntervalMinutes).GreaterThanOrEqualTo(60)
            .WithMessage("Scan interval must be at least 60 minutes");
    }
}
