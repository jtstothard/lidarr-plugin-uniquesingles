using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Represents the result of comparing a single's track against an album/EP track.
/// </summary>
/// <param name="SingleTrack">The track from the single being checked.</param>
/// <param name="MatchedTrack">The matching track found on an album/EP (null if no match).</param>
/// <param name="Tier">The match confidence tier.</param>
/// <param name="Confidence">Numeric confidence score (0.0–1.0).</param>
/// <param name="Reason">Human-readable explanation of why this match was made or not.</param>
public record MatchResult(
    Track SingleTrack,
    Track? MatchedTrack,
    MatchTier Tier,
    double Confidence,
    string Reason);
