namespace UniqueSingles.Matching;

/// <summary>
/// Represents the confidence tier of a track match.
/// Higher tiers indicate stronger matches with more confidence.
/// </summary>
public enum MatchTier
{
    /// <summary>
    /// Tier 1: Exact MusicBrainz Recording MBID match. ~99.9% confidence.
    /// Safe to auto-unmonitor and delete.
    /// </summary>
    Tier1_Mbid,

    /// <summary>
    /// Tier 2: Title + Duration match (normalized title match + duration within ±3s). ~95% confidence.
    /// Safe to auto-unmonitor and delete.
    /// </summary>
    Tier2_TitleDuration,

    /// <summary>
    /// Tier 3: Title-only match (normalized title match, no duration match). ~80% confidence.
    /// Flag for review, do NOT auto-delete.
    /// </summary>
    Tier3_TitleOnly,

    /// <summary>
    /// No match found. The track is unique to the single.
    /// </summary>
    NoMatch
}
