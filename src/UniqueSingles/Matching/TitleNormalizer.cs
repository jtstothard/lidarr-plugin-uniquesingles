using System.Text.RegularExpressions;

namespace UniqueSingles.Matching;

/// <summary>
/// Normalizes track titles for comparison purposes.
/// Strips featuring annotations and trailing punctuation while preserving
/// version suffixes like "(acoustic)", "(remix)", "(radio edit)" which
/// indicate different recordings.
/// </summary>
public static class TitleNormalizer
{
    // Matches "(feat. Artist)" or "(featuring Artist)" — entire parenthetical
    private static readonly Regex FeaturingPattern = new(
        @"\(feat\.?\s.*?\)|\(featuring\s.*?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches trailing punctuation: ! ? . ; :
    private static readonly Regex TrailingPunctuationPattern = new(
        @"[!?.;:]+$",
        RegexOptions.Compiled);

    // Matches multiple consecutive whitespace characters
    private static readonly Regex WhitespaceCollapsePattern = new(
        @"\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a track title for comparison.
    /// <list type="bullet">
    ///   <item>Lowercase invariant</item>
    ///   <item>Strip featuring annotations: "(feat. Artist)" or "(featuring Artist)"</item>
    ///   <item>Strip trailing punctuation: ! ? . ; :</item>
    ///   <item>Collapse whitespace</item>
    ///   <item>Trim</item>
    /// </list>
    /// Does NOT strip version suffixes like "(acoustic)", "(remix)", "(radio edit)".
    /// </summary>
    /// <param name="title">The raw track title.</param>
    /// <returns>Normalized title, or empty string if input is null/empty.</returns>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // Lowercase invariant
        var result = title.ToLowerInvariant();

        // Strip featuring annotations (must happen before trailing punctuation strip
        // to handle cases like "Song (feat. Artist)!")
        result = FeaturingPattern.Replace(result, string.Empty);

        // Strip trailing punctuation
        result = TrailingPunctuationPattern.Replace(result, string.Empty);

        // Collapse whitespace
        result = WhitespaceCollapsePattern.Replace(result, " ");

        // Trim
        return result.Trim();
    }
}
