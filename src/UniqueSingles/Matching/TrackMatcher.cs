using System.Linq;
using System.Collections.Generic;
using System;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Core matching engine that implements the 3-tier cascade matching strategy
/// and multi-track protection rule. Takes Track objects in, returns MatchResult
/// records out. Zero Lidarr infrastructure dependency beyond the Track model.
/// </summary>
public static class TrackMatcher
{
    /// <summary>
    /// Duration tolerance in milliseconds for Tier 2 matching (±3 seconds).
    /// </summary>
    public const int DurationToleranceMs = 3000;

    /// <summary>
    /// Confidence values for each match tier.
    /// </summary>
    public static class Confidence
    {
        public const double Tier1 = 0.999;
        public const double Tier2 = 0.95;
        public const double Tier3 = 0.80;
        public const double NoMatch = 0.0;
    }

    /// <summary>
    /// Finds the best match for a single's track against a list of album/EP tracks.
    /// Implements the 3-tier cascade: MBID → title+duration → title-only → no match.
    /// Only considers album tracks where HasFile is true (R012).
    /// Uses the default 3000 ms duration tolerance.
    /// </summary>
    /// <param name="singleTrack">The track from the single to match.</param>
    /// <param name="albumTracks">All tracks from monitored albums/EPs to compare against.</param>
    /// <returns>The best MatchResult found, or a NoMatch result if nothing matched.</returns>
    public static MatchResult FindBestMatch(Track singleTrack, List<Track> albumTracks)
    {
        return FindBestMatch(singleTrack, albumTracks, DurationToleranceMs);
    }

    /// <summary>
    /// Finds the best match for a single's track against a list of album/EP tracks.
    /// Implements the 3-tier cascade: MBID → title+duration → title-only → no match.
    /// Only considers album tracks where HasFile is true (R012).
    /// </summary>
    /// <param name="singleTrack">The track from the single to match.</param>
    /// <param name="albumTracks">All tracks from monitored albums/EPs to compare against.</param>
    /// <param name="durationToleranceMs">Duration tolerance in milliseconds for Tier 2 matching.</param>
    /// <returns>The best MatchResult found, or a NoMatch result if nothing matched.</returns>
    public static MatchResult FindBestMatch(Track singleTrack, List<Track> albumTracks, int durationToleranceMs)
    {
        if (singleTrack == null)
        {
            throw new ArgumentNullException(nameof(singleTrack));
        }

        if (albumTracks == null)
        {
            return new MatchResult(singleTrack, null, MatchTier.NoMatch, Confidence.NoMatch, "No album tracks provided.");
        }

        // Filter to only tracks with files (R012)
        var tracksWithFiles = albumTracks.Where(t => t.HasFile).ToList();

        if (tracksWithFiles.Count == 0)
        {
            return new MatchResult(
                singleTrack,
                null,
                MatchTier.NoMatch,
                Confidence.NoMatch,
                "No album tracks with files found.");
        }

        // Tier 1: Exact MBID match
        var tier1Match = FindTier1Match(singleTrack, tracksWithFiles);
        if (tier1Match != null)
        {
            return tier1Match;
        }

        // Tier 2: Title + Duration match
        var tier2Match = FindTier2Match(singleTrack, tracksWithFiles, durationToleranceMs);
        if (tier2Match != null)
        {
            return tier2Match;
        }

        // Tier 3: Title-only match
        var tier3Match = FindTier3Match(singleTrack, tracksWithFiles);
        if (tier3Match != null)
        {
            return tier3Match;
        }

        // No match
        return new MatchResult(
            singleTrack,
            null,
            MatchTier.NoMatch,
            Confidence.NoMatch,
            $"No match found for '{singleTrack.Title}' across {tracksWithFiles.Count} album tracks.");
    }

    /// <summary>
    /// Checks whether an entire single release is redundant against monitored albums/EPs.
    /// A single is only redundant if ALL its tracks have at least a Tier 2 match
    /// (multi-track protection rule — R004). Tier 3 matches flag for review but
    /// do NOT make the single redundant.
    /// Uses the default 3000 ms duration tolerance.
    /// </summary>
    /// <param name="singleTracks">All tracks on the single release.</param>
    /// <param name="albumTracks">All tracks from monitored albums/EPs.</param>
    /// <returns>A SingleRedundancyCheck with per-track results and overall assessment.</returns>
    public static SingleRedundancyCheck CheckSingle(List<Track> singleTracks, List<Track> albumTracks)
    {
        return CheckSingle(singleTracks, albumTracks, DurationToleranceMs);
    }

    /// <summary>
    /// Checks whether an entire single release is redundant against monitored albums/EPs.
    /// A single is only redundant if ALL its tracks have at least a Tier 2 match
    /// (multi-track protection rule — R004). Tier 3 matches flag for review but
    /// do NOT make the single redundant.
    /// </summary>
    /// <param name="singleTracks">All tracks on the single release.</param>
    /// <param name="albumTracks">All tracks from monitored albums/EPs.</param>
    /// <param name="durationToleranceMs">Duration tolerance in milliseconds for Tier 2 matching.</param>
    /// <returns>A SingleRedundancyCheck with per-track results and overall assessment.</returns>
    public static SingleRedundancyCheck CheckSingle(List<Track> singleTracks, List<Track> albumTracks, int durationToleranceMs)
    {
        if (singleTracks == null || singleTracks.Count == 0)
        {
            return new SingleRedundancyCheck(
                false,
                new List<MatchResult>(),
                "No single tracks provided.");
        }

        if (albumTracks == null || albumTracks.Count == 0)
        {
            var noAlbumResults = singleTracks.Select(t =>
                new MatchResult(t, null, MatchTier.NoMatch, Confidence.NoMatch, "No album tracks provided.")).ToList();

            return new SingleRedundancyCheck(
                false,
                noAlbumResults,
                "No album tracks to compare against.");
        }

        // Find best match for each single track
        var trackResults = singleTracks
            .Select(t => FindBestMatch(t, albumTracks, durationToleranceMs))
            .ToList();

        // Determine overall redundancy
        var hasNoMatch = trackResults.Any(r => r.Tier == MatchTier.NoMatch);
        var hasTier3Only = trackResults.Any(r => r.Tier == MatchTier.Tier3_TitleOnly);
        var allHighConfidence = trackResults.All(r =>
            r.Tier == MatchTier.Tier1_Mbid || r.Tier == MatchTier.Tier2_TitleDuration);

        bool isRedundant;
        string summaryReason;

        if (hasNoMatch)
        {
            // Multi-track protection: if ANY track has no match, single is NOT redundant
            var unmatchedTitles = trackResults
                .Where(r => r.Tier == MatchTier.NoMatch)
                .Select(r => r.SingleTrack.Title)
                .ToList();

            isRedundant = false;
            summaryReason = trackResults.Count == 1
                ? $"Track '{unmatchedTitles[0]}' not found on any monitored album."
                : $"{unmatchedTitles.Count} of {trackResults.Count} tracks not found on monitored albums: {string.Join(", ", unmatchedTitles)}. Single has exclusive content.";
        }
        else if (hasTier3Only)
        {
            // Tier 3 matches only — flag for review, don't auto-delete
            var tier3Titles = trackResults
                .Where(r => r.Tier == MatchTier.Tier3_TitleOnly)
                .Select(r => r.SingleTrack.Title)
                .ToList();

            isRedundant = false;
            summaryReason = trackResults.Count == 1
                ? $"Track '{tier3Titles[0]}' has title-only match (Tier 3). Manual review required — duration mismatch suggests it may be a different version."
                : $"{tier3Titles.Count} tracks have title-only matches (Tier 3): {string.Join(", ", tier3Titles)}. Manual review required — duration mismatches suggest different versions.";
        }
        else if (allHighConfidence)
        {
            // All tracks matched at Tier 1 or Tier 2 — safe to consider redundant
            var tier1Count = trackResults.Count(r => r.Tier == MatchTier.Tier1_Mbid);
            var tier2Count = trackResults.Count(r => r.Tier == MatchTier.Tier2_TitleDuration);

            isRedundant = true;
            summaryReason = trackResults.Count == 1
                ? $"All tracks matched. Tier 1: {tier1Count}, Tier 2: {tier2Count}. Safe to unmonitor and delete."
                : $"All {trackResults.Count} tracks matched (Tier 1: {tier1Count}, Tier 2: {tier2Count}). Single is fully redundant — safe to unmonitor and delete.";
        }
        else
        {
            // Shouldn't reach here, but defensive fallback
            isRedundant = false;
            summaryReason = "Unexpected match result combination. Manual review recommended.";
        }

        return new SingleRedundancyCheck(isRedundant, trackResults, summaryReason);
    }

    /// <summary>
    /// Checks whether a single is fully Tier-1 redundant (all tracks have exact MBID matches).
    /// This does NOT require tracks to have files — MBID matching is sufficient for safety.
    /// Used to unmonitor singles even when no files are downloaded yet.
    /// </summary>
    /// <param name="singleTracks">All tracks on the single release.</param>
    /// <param name="albumTracks">All tracks from monitored albums/EPs (including those without files).</param>
    /// <returns>True if all single tracks have exact MBID matches with album tracks.</returns>
    public static bool IsTier1OnlyRedundant(List<Track> singleTracks, List<Track> albumTracks)
    {
        if (singleTracks == null || singleTracks.Count == 0)
        {
            return false;
        }

        if (albumTracks == null || albumTracks.Count == 0)
        {
            return false;
        }

        // Check if every single track has an exact MBID match
        foreach (var singleTrack in singleTracks)
        {
            if (string.IsNullOrWhiteSpace(singleTrack.ForeignRecordingId))
            {
                return false; // No MBID available, can't be Tier-1 match
            }

            bool hasMatch = false;
            foreach (var albumTrack in albumTracks)
            {
                if (!string.IsNullOrWhiteSpace(albumTrack.ForeignRecordingId) &&
                    singleTrack.ForeignRecordingId == albumTrack.ForeignRecordingId)
                {
                    hasMatch = true;
                    break;
                }
            }

            if (!hasMatch)
            {
                return false; // At least one track lacks MBID match
            }
        }

        return true; // All tracks have exact MBID matches
    }

    private static MatchResult? FindTier1Match(Track singleTrack, List<Track> albumTracks)
    {
        if (string.IsNullOrWhiteSpace(singleTrack.ForeignRecordingId))
        {
            return null;
        }

        foreach (var albumTrack in albumTracks)
        {
            if (string.IsNullOrWhiteSpace(albumTrack.ForeignRecordingId))
            {
                continue;
            }

            // Case-sensitive MBID comparison (standard UUID format)
            if (singleTrack.ForeignRecordingId == albumTrack.ForeignRecordingId)
            {
                return new MatchResult(
                    singleTrack,
                    albumTrack,
                    MatchTier.Tier1_Mbid,
                    Confidence.Tier1,
                    $"Exact MusicBrainz Recording MBID match: {singleTrack.ForeignRecordingId}");
            }
        }

        return null;
    }

    private static MatchResult? FindTier2Match(Track singleTrack, List<Track> albumTracks, int durationToleranceMs)
    {
        var normalizedSingleTitle = TitleNormalizer.Normalize(singleTrack.Title);

        if (string.IsNullOrEmpty(normalizedSingleTitle))
        {
            return null;
        }

        foreach (var albumTrack in albumTracks)
        {
            var normalizedAlbumTitle = TitleNormalizer.Normalize(albumTrack.Title);

            if (normalizedSingleTitle != normalizedAlbumTitle)
            {
                continue;
            }

            // Both durations must be > 0 to qualify for Tier 2
            if (singleTrack.Duration <= 0 || albumTrack.Duration <= 0)
            {
                continue;
            }

            var durationDiff = Math.Abs(singleTrack.Duration - albumTrack.Duration);

            if (durationDiff <= durationToleranceMs)
            {
                return new MatchResult(
                    singleTrack,
                    albumTrack,
                    MatchTier.Tier2_TitleDuration,
                    Confidence.Tier2,
                    $"Title + Duration match (normalized: '{normalizedSingleTitle}', " +
                    $"duration diff: {durationDiff}ms ≤ {durationToleranceMs}ms)");
            }
        }

        return null;
    }

    private static MatchResult? FindTier3Match(Track singleTrack, List<Track> albumTracks)
    {
        var normalizedSingleTitle = TitleNormalizer.Normalize(singleTrack.Title);

        if (string.IsNullOrEmpty(normalizedSingleTitle))
        {
            return null;
        }

        foreach (var albumTrack in albumTracks)
        {
            var normalizedAlbumTitle = TitleNormalizer.Normalize(albumTrack.Title);

            if (normalizedSingleTitle == normalizedAlbumTitle)
            {
                var durationInfo = singleTrack.Duration > 0 && albumTrack.Duration > 0
                    ? $"Duration mismatch: {singleTrack.Duration}ms vs {albumTrack.Duration}ms (diff: {Math.Abs(singleTrack.Duration - albumTrack.Duration)}ms)"
                    : "Duration data missing";

                return new MatchResult(
                    singleTrack,
                    albumTrack,
                    MatchTier.Tier3_TitleOnly,
                    Confidence.Tier3,
                    $"Title-only match (normalized: '{normalizedSingleTitle}'). {durationInfo}. Flagged for review.");
            }
        }

        return null;
    }
}
