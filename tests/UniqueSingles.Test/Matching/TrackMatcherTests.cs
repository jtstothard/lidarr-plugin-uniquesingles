using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test.Matching;

public class TrackMatcherTests
{
    // Helper to create a Track with the fields we need for matching
    private static Track CreateTrack(
        string title,
        int duration = 0,
        string? foreignRecordingId = null,
        bool hasFile = true)
    {
        var track = new Track
        {
            Title = title,
            Duration = duration,
            ForeignRecordingId = foreignRecordingId ?? string.Empty,
        };

        // HasFile is computed from TrackFileId (> 0 means has file)
        if (hasFile)
        {
            track.TrackFileId = 1;
        }

        return track;
    }

    // ============================================================
    // EC1: Multi-track single with exclusive B-side → NOT redundant
    // ============================================================

    [Fact]
    public void EC1_MultiTrackSingle_WithExclusiveBSide_NotRedundant()
    {
        // "Bitter" single has 2 tracks:
        // - Track 1: "Bitter" — on an album
        // - Track 2: "Die Young (acoustic)" — NOT on any album (exclusive B-side)
        var singleTracks = new List<Track>
        {
            CreateTrack("Bitter", duration: 200000, foreignRecordingId: "51bd7a30-0000-0000-0000-000000000001"),
            CreateTrack("Die Young (acoustic)", duration: 245000, foreignRecordingId: "60ca4db8-0000-0000-0000-000000000002"),
        };

        var albumTracks = new List<Track>
        {
            // "Bitter" exists on an album
            CreateTrack("Bitter", duration: 200000, foreignRecordingId: "51bd7a30-0000-0000-0000-000000000001"),
            // "Die Young (acoustic)" does NOT exist on any album
        };

        var result = TrackMatcher.CheckSingle(singleTracks, albumTracks);

        Assert.False(result.IsRedundant);
        Assert.Equal(2, result.TrackResults.Count);
        Assert.Contains(result.TrackResults, r => r.Tier == MatchTier.NoMatch);
    }

    // ============================================================
    // EC2: Pink Pony Club — different MBIDs, same song (Tier 2)
    // ============================================================

    [Fact]
    public void EC2_PinkPonyClub_DifferentMBIDs_SameTitleDuration_Tier2Match()
    {
        var singleTrack = CreateTrack(
            "Pink Pony Club",
            duration: 258000,
            foreignRecordingId: "8331bad7-f2fe-4961-a6ef-b0f87bd4e30f");

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "Pink Pony Club",
                duration: 258000,
                foreignRecordingId: "1f79a002-85d2-424a-9b4d-a1407c8504c5"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
        Assert.Equal(0.95, result.Confidence);
        Assert.NotNull(result.MatchedTrack);
    }

    [Fact]
    public void EC2_PinkPonyClub_SingleIsRedundant_WhenAllTracksTier2()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack(
                "Pink Pony Club",
                duration: 258000,
                foreignRecordingId: "8331bad7-f2fe-4961-a6ef-b0f87bd4e30f"),
        };

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "Pink Pony Club",
                duration: 258000,
                foreignRecordingId: "1f79a002-85d2-424a-9b4d-a1407c8504c5"),
        };

        var result = TrackMatcher.CheckSingle(singleTracks, albumTracks);

        Assert.True(result.IsRedundant);
        Assert.All(result.TrackResults, r => Assert.True(r.Tier == MatchTier.Tier2_TitleDuration));
    }

    // ============================================================
    // EC3: Remix — different recording (No match)
    // ============================================================

    [Fact]
    public void EC3_Remix_DifferentRecording_NoMatch()
    {
        // Good Hurt (original) vs Good Hurt (Aevion remix) — different recordings
        var singleTrack = CreateTrack(
            "Good Hurt (Aevion remix)",
            duration: 220000,
            foreignRecordingId: "580646ed-0000-0000-0000-000000000001");

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "Good Hurt",
                duration: 215000,
                foreignRecordingId: "877d1dd9-0000-0000-0000-000000000002"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.NoMatch, result.Tier);
    }

    // ============================================================
    // EC4: Acoustic version — different recording, different duration (No match)
    // ============================================================

    [Fact]
    public void EC4_AcousticVersion_DifferentRecordingAndDuration_NoMatch()
    {
        var singleTrack = CreateTrack(
            "Die Young (acoustic)",
            duration: 245000,
            foreignRecordingId: "60ca4db8-0000-0000-0000-000000000001");

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "Die Young",
                duration: 225901,
                foreignRecordingId: "91d3eb2a-0000-0000-0000-000000000002"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.NoMatch, result.Tier);
    }

    // ============================================================
    // EC5: EP/Single name collision — different tracks (No match)
    // ============================================================

    [Fact]
    public void EC5_EP_SingleNameCollision_NoMatch()
    {
        // Single "School Nights" track doesn't appear on EP "School Nights"
        var singleTrack = CreateTrack(
            "School Nights",
            duration: 200000,
            foreignRecordingId: "05bf6409-0000-0000-0000-000000000001");

        var albumTracks = new List<Track>
        {
            CreateTrack("Die Young", duration: 225901, foreignRecordingId: "91d3eb2a-0000-0000-0000-000000000002"),
            CreateTrack("Good Hurt", duration: 215000, foreignRecordingId: "877d1dd9-0000-0000-0000-000000000003"),
            CreateTrack("Meantime", duration: 180000, foreignRecordingId: "aaaa0000-0000-0000-0000-000000000004"),
            CreateTrack("Sugar High", duration: 195000, foreignRecordingId: "bbbb0000-0000-0000-0000-000000000005"),
            CreateTrack("Bad for You", duration: 210000, foreignRecordingId: "cccc0000-0000-0000-0000-000000000006"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.NoMatch, result.Tier);
    }

    // ============================================================
    // EC6: Album not downloaded (HasFile = false) → skip
    // ============================================================

    [Fact]
    public void EC6_AlbumNotDownloaded_HasFileFalse_Skipped()
    {
        var singleTrack = CreateTrack(
            "HOT TO GO!",
            duration: 185000,
            foreignRecordingId: "e619cd9d-ee35-4e37-8ee1-51b992ab95d7");

        // Album track exists but HasFile = false (not downloaded)
        var albumTracks = new List<Track>
        {
            CreateTrack(
                "HOT TO GO!",
                duration: 185000,
                foreignRecordingId: "e619cd9d-ee35-4e37-8ee1-51b992ab95d7",
                hasFile: false),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.NoMatch, result.Tier);
    }

    // ============================================================
    // EC7: Partially downloaded album — only HasFile=true tracks used
    // ============================================================

    [Fact]
    public void EC7_PartiallyDownloadedAlbum_OnlyHasFileTracksUsed()
    {
        var singleTrack = CreateTrack(
            "Good Hurt",
            duration: 215000,
            foreignRecordingId: "877d1dd9-0000-0000-0000-000000000001");

        var albumTracks = new List<Track>
        {
            // This track is downloaded — should match
            CreateTrack(
                "Good Hurt",
                duration: 215000,
                foreignRecordingId: "877d1dd9-0000-0000-0000-000000000001",
                hasFile: true),
            // These tracks are NOT downloaded — should be skipped
            CreateTrack(
                "Die Young",
                duration: 225901,
                foreignRecordingId: "91d3eb2a-0000-0000-0000-000000000002",
                hasFile: false),
            CreateTrack(
                "Meantime",
                duration: 180000,
                foreignRecordingId: "aaaa0000-0000-0000-0000-000000000003",
                hasFile: false),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        // Should match the one downloaded track
        Assert.Equal(MatchTier.Tier1_Mbid, result.Tier);
        Assert.NotNull(result.MatchedTrack);
        Assert.Equal("877d1dd9-0000-0000-0000-000000000001", result.MatchedTrack!.ForeignRecordingId);
    }

    // ============================================================
    // EC8: Duplicate singles — each checked independently
    // ============================================================

    [Fact]
    public void EC8_DuplicateSingles_EachCheckedIndependently()
    {
        var singleTrack1 = CreateTrack(
            "The Giver",
            duration: 200000,
            foreignRecordingId: "57887697-0000-0000-0000-000000000001");

        var singleTrack2 = CreateTrack(
            "The Giver",
            duration: 200000,
            foreignRecordingId: "57887697-0000-0000-0000-000000000001");

        // No album tracks yet — neither should be redundant
        var albumTracks = new List<Track>();

        var result1 = TrackMatcher.FindBestMatch(singleTrack1, albumTracks);
        var result2 = TrackMatcher.FindBestMatch(singleTrack2, albumTracks);

        Assert.Equal(MatchTier.NoMatch, result1.Tier);
        Assert.Equal(MatchTier.NoMatch, result2.Tier);

        // Now add an album track — both should match independently
        var albumTracksWithMatch = new List<Track>
        {
            CreateTrack("The Giver", duration: 200000, foreignRecordingId: "57887697-0000-0000-0000-000000000001"),
        };

        var result1WithAlbum = TrackMatcher.FindBestMatch(singleTrack1, albumTracksWithMatch);
        var result2WithAlbum = TrackMatcher.FindBestMatch(singleTrack2, albumTracksWithMatch);

        Assert.Equal(MatchTier.Tier1_Mbid, result1WithAlbum.Tier);
        Assert.Equal(MatchTier.Tier1_Mbid, result2WithAlbum.Tier);
    }

    // ============================================================
    // EC9: Featuring annotations — stripped, matches correctly
    // ============================================================

    [Fact]
    public void EC9_FeaturingAnnotation_StrippedAndMatches()
    {
        // Album: "Parting Gift (feat. Brendan Kelly)"
        // Single: "Parting Gift"
        // After normalization, both → "parting gift"
        var singleTrack = CreateTrack(
            "Parting Gift",
            duration: 210000,
            foreignRecordingId: "single-mbid-0001");

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "Parting Gift (feat. Brendan Kelly)",
                duration: 210000,
                foreignRecordingId: "album-mbid-0001"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        // Tier 2 because MBIDs differ, but title+duration match
        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
        Assert.Equal(0.95, result.Confidence);
    }

    // ============================================================
    // EC10: Case/punctuation differences → normalized, matches
    // ============================================================

    [Fact]
    public void EC10_CaseAndPunctuationDifferences_NormalizedAndMatches()
    {
        // Album: "HOT TO GO!" (all caps + exclamation)
        // Single: "Hot to Go!" (mixed case + exclamation)
        var singleTrack = CreateTrack(
            "Hot to Go!",
            duration: 184000,
            foreignRecordingId: "single-mbid-hotogo");

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "HOT TO GO!",
                duration: 185000,
                foreignRecordingId: "e619cd9d-ee35-4e37-8ee1-51b992ab95d7"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        // Tier 2: title matches after normalization, duration diff = 1000ms ≤ 3000ms
        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
    }

    // ============================================================
    // EC11: Duration discrepancy (1s diff, same recording MBID) → Tier 1
    // ============================================================

    [Fact]
    public void EC11_DurationDiscrepancy_SameMBID_Tier1Match()
    {
        // Same recording MBID but 1 second duration difference
        var singleTrack = CreateTrack(
            "HOT TO GO!",
            duration: 184000,
            foreignRecordingId: "e619cd9d-ee35-4e37-8ee1-51b992ab95d7");

        var albumTracks = new List<Track>
        {
            CreateTrack(
                "HOT TO GO!",
                duration: 185000,
                foreignRecordingId: "e619cd9d-ee35-4e37-8ee1-51b992ab95d7"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        // Tier 1: exact MBID match, duration difference is irrelevant
        Assert.Equal(MatchTier.Tier1_Mbid, result.Tier);
        Assert.Equal(0.999, result.Confidence);
    }

    // ============================================================
    // Tier 1 exact match (basic)
    // ============================================================

    [Fact]
    public void FindBestMatch_SameMBID_ReturnsTier1()
    {
        var singleTrack = CreateTrack(
            "Any Title",
            duration: 200000,
            foreignRecordingId: "abc-123");

        var albumTracks = new List<Track>
        {
            CreateTrack("Any Title", duration: 200000, foreignRecordingId: "abc-123"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.Tier1_Mbid, result.Tier);
        Assert.Equal(0.999, result.Confidence);
        Assert.Equal(singleTrack, result.SingleTrack);
        Assert.NotNull(result.MatchedTrack);
    }

    // ============================================================
    // Tier 2 title + duration match
    // ============================================================

    [Fact]
    public void FindBestMatch_SameTitleDuration_DifferentMBID_ReturnsTier2()
    {
        var singleTrack = CreateTrack(
            "Song Name",
            duration: 200000,
            foreignRecordingId: "single-id");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song Name", duration: 201000, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
        Assert.Equal(0.95, result.Confidence);
    }

    // ============================================================
    // Tier 3 — title only, duration mismatch
    // ============================================================

    [Fact]
    public void FindBestMatch_SameTitle_DifferentDuration_ReturnsTier3()
    {
        var singleTrack = CreateTrack(
            "Song Name",
            duration: 200000,
            foreignRecordingId: "single-id");

        // Duration difference > 3000ms
        var albumTracks = new List<Track>
        {
            CreateTrack("Song Name", duration: 210000, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.Tier3_TitleOnly, result.Tier);
        Assert.Equal(0.80, result.Confidence);
    }

    [Fact]
    public void FindBestMatch_SameTitle_ZeroDuration_ReturnsTier3()
    {
        var singleTrack = CreateTrack(
            "Song Name",
            duration: 0,
            foreignRecordingId: "single-id");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song Name", duration: 200000, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        // Duration must be > 0 for Tier 2, so falls to Tier 3
        Assert.Equal(MatchTier.Tier3_TitleOnly, result.Tier);
    }

    // ============================================================
    // Tier 3 → IsRedundant = false (R011)
    // ============================================================

    [Fact]
    public void CheckSingle_Tier3Only_NotRedundant_FlagForReview()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack("Song Name", duration: 200000, foreignRecordingId: "single-id"),
        };

        // Duration mismatch → Tier 3
        var albumTracks = new List<Track>
        {
            CreateTrack("Song Name", duration: 210000, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.CheckSingle(singleTracks, albumTracks);

        Assert.False(result.IsRedundant);
        Assert.Contains("review", result.SummaryReason, StringComparison.OrdinalIgnoreCase);
    }

    // ============================================================
    // Idempotency (R010)
    // ============================================================

    [Fact]
    public void FindBestMatch_Idempotent_SameInputsSameOutputs()
    {
        var singleTrack = CreateTrack(
            "Test Song",
            duration: 180000,
            foreignRecordingId: "test-id-1");

        var albumTracks = new List<Track>
        {
            CreateTrack("Test Song", duration: 180000, foreignRecordingId: "test-id-1"),
            CreateTrack("Other Song", duration: 200000, foreignRecordingId: "test-id-2"),
        };

        var result1 = TrackMatcher.FindBestMatch(singleTrack, albumTracks);
        var result2 = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(result1.Tier, result2.Tier);
        Assert.Equal(result1.Confidence, result2.Confidence);
        Assert.Equal(result1.Reason, result2.Reason);
    }

    // ============================================================
    // Null/empty inputs
    // ============================================================

    [Fact]
    public void FindBestMatch_NullSingleTrack_ThrowsArgumentNullException()
    {
        var albumTracks = new List<Track> { CreateTrack("Song", 200000) };

        Assert.Throws<ArgumentNullException>(() => TrackMatcher.FindBestMatch(null!, albumTracks));
    }

    [Fact]
    public void FindBestMatch_NullAlbumTracks_ReturnsNoMatch()
    {
        var singleTrack = CreateTrack("Song", 200000, "mbid-1");

        var result = TrackMatcher.FindBestMatch(singleTrack, null!);

        Assert.Equal(MatchTier.NoMatch, result.Tier);
    }

    [Fact]
    public void FindBestMatch_EmptyAlbumTracks_ReturnsNoMatch()
    {
        var singleTrack = CreateTrack("Song", 200000, "mbid-1");

        var result = TrackMatcher.FindBestMatch(singleTrack, new List<Track>());

        Assert.Equal(MatchTier.NoMatch, result.Tier);
    }

    [Fact]
    public void CheckSingle_NullSingleTracks_ReturnsNotRedundant()
    {
        var result = TrackMatcher.CheckSingle(null!, new List<Track>());

        Assert.False(result.IsRedundant);
    }

    [Fact]
    public void CheckSingle_EmptySingleTracks_ReturnsNotRedundant()
    {
        var result = TrackMatcher.CheckSingle(new List<Track>(), new List<Track>());

        Assert.False(result.IsRedundant);
    }

    [Fact]
    public void CheckSingle_NullAlbumTracks_ReturnsNotRedundant()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack("Song", 200000, "mbid-1"),
        };

        var result = TrackMatcher.CheckSingle(singleTracks, null!);

        Assert.False(result.IsRedundant);
        Assert.All(result.TrackResults, r => Assert.Equal(MatchTier.NoMatch, r.Tier));
    }

    // ============================================================
    // Empty ForeignRecordingId — should skip Tier 1, fall to Tier 2/3
    // ============================================================

    [Fact]
    public void FindBestMatch_EmptyForeignRecordingId_SkipsTier1()
    {
        var singleTrack = CreateTrack("Song Name", duration: 200000, foreignRecordingId: "");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song Name", duration: 200000, foreignRecordingId: ""),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        // Empty recording IDs should not match at Tier 1
        Assert.NotEqual(MatchTier.Tier1_Mbid, result.Tier);
        // Should fall to Tier 2
        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
    }

    // ============================================================
    // Multi-track single where ALL tracks match → IS redundant
    // ============================================================

    [Fact]
    public void CheckSingle_AllTracksMatchedHighConfidence_IsRedundant()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack("Track A", duration: 180000, foreignRecordingId: "mbid-a"),
            CreateTrack("Track B", duration: 200000, foreignRecordingId: "mbid-b"),
        };

        var albumTracks = new List<Track>
        {
            CreateTrack("Track A", duration: 180000, foreignRecordingId: "mbid-a"),
            CreateTrack("Track B", duration: 200000, foreignRecordingId: "mbid-b"),
        };

        var result = TrackMatcher.CheckSingle(singleTracks, albumTracks);

        Assert.True(result.IsRedundant);
        Assert.Equal(2, result.TrackResults.Count);
        Assert.All(result.TrackResults, r =>
            Assert.True(r.Tier == MatchTier.Tier1_Mbid || r.Tier == MatchTier.Tier2_TitleDuration));
    }

    // ============================================================
    // Duration at exact boundary (±3000ms)
    // ============================================================

    [Fact]
    public void FindBestMatch_DurationExactlyAtTolerance_Tier2()
    {
        var singleTrack = CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 203000, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
    }

    [Fact]
    public void FindBestMatch_DurationOneMsOverTolerance_Tier3()
    {
        var singleTrack = CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 203001, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks);

        Assert.Equal(MatchTier.Tier3_TitleOnly, result.Tier);
    }

    // ============================================================
    // Tolerance-aware overloads
    // ============================================================

    [Fact]
    public void FindBestMatch_CustomTolerance_UsesProvidedValue()
    {
        var singleTrack = CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 250000, foreignRecordingId: "album-id"),
        };

        // 50000ms tolerance should match (diff = 50000ms)
        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks, 50000);

        Assert.Equal(MatchTier.Tier2_TitleDuration, result.Tier);
    }

    [Fact]
    public void FindBestMatch_StrictTolerance_RejectsLongerDuration()
    {
        var singleTrack = CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id");

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 250000, foreignRecordingId: "album-id"),
        };

        // 1000ms tolerance should NOT match (diff = 50000ms)
        var result = TrackMatcher.FindBestMatch(singleTrack, albumTracks, 1000);

        Assert.Equal(MatchTier.Tier3_TitleOnly, result.Tier);
    }

    [Fact]
    public void CheckSingle_CustomTolerance_ChangesTier2Behavior()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id"),
        };

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 250000, foreignRecordingId: "album-id"),
        };

        // Default tolerance (3000ms) should NOT match → Tier 3 → not redundant
        var defaultResult = TrackMatcher.CheckSingle(singleTracks, albumTracks);
        Assert.False(defaultResult.IsRedundant);

        // 50000ms tolerance should match → Tier 2 → redundant
        var tolerantResult = TrackMatcher.CheckSingle(singleTracks, albumTracks, 50000);
        Assert.True(tolerantResult.IsRedundant);
    }

    [Fact]
    public void CheckSingle_DefaultOverload_Uses3000msTolerance()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id"),
        };

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 203001, foreignRecordingId: "album-id"),
        };

        var result = TrackMatcher.CheckSingle(singleTracks, albumTracks);

        // Diff = 3001ms > 3000ms → Tier 3
        Assert.False(result.IsRedundant);
    }

    [Fact]
    public void CheckSingle_CustomTolerance_ExactBoundary()
    {
        var singleTracks = new List<Track>
        {
            CreateTrack("Song", duration: 200000, foreignRecordingId: "single-id"),
        };

        var albumTracks = new List<Track>
        {
            CreateTrack("Song", duration: 250000, foreignRecordingId: "album-id"),
        };

        // Exact tolerance boundary (50000ms) should match
        var result = TrackMatcher.CheckSingle(singleTracks, albumTracks, 50000);
        Assert.True(result.IsRedundant);
    }
}
