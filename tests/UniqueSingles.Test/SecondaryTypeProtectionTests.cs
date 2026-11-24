using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Secondary-type handling beyond the source-side exclusion of #31:
/// (1) a single that itself carries Live/Remix/DJ-mix/Demo is never a cleanup candidate;
/// (2) a source release carrying any other secondary type (Compilation, Soundtrack, ...) only
///     contributes Tier-1 exact-MBID evidence; (3) unreadable (null) secondary types are
///     conservative in both roles.
/// </summary>
public partial class SingleCleanupServiceTests
{
    private static SingleCleanupOptions MetadataOptions(Tier3Action tier3 = Tier3Action.FlagOnly) =>
        new(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, tier3);

    // route: 0 = scan, 1 = album import, 2 = single self-check
    private static CleanupResult SecondaryRun(SingleCleanupService service, int route, Album source, Album single, SingleCleanupOptions options) => route switch
    {
        0 => service.ScanArtistWithOptions(Artist(), options),
        1 => service.CleanupWithOptions(Artist(), source, options),
        _ => service.CleanupSingleSelfCheckWithOptions(Artist(), single, options),
    };

    private sealed record SecondaryHarness(
        Album Source, Album Single, RecordingAlbumService Albums, RecordingDeleteMediaFiles Deletes, SingleCleanupService Service);

    private static SecondaryHarness SecondaryScenario(
        List<SecondaryAlbumType>? sourceTypes, List<SecondaryAlbumType>? singleTypes,
        string sourceMbid, string singleMbid, int sourceDuration, int singleDuration, bool setSourceTypes = true, bool setSingleTypes = true)
    {
        var source = Album(100, "Studio Album", "Album");
        if (setSourceTypes) source.SecondaryTypes = sourceTypes!;
        var single = Album(200, "Song", "Single");
        if (setSingleTypes) single.SecondaryTypes = singleTypes!;
        var albums = new RecordingAlbumService(source, single);
        var tracks = new RecordingTrackService()
            .WithTracks(source.Id, Track("Song", sourceDuration, sourceMbid, fileId: 11))
            .WithTracks(single.Id, Track("Song", singleDuration, singleMbid, fileId: 21));
        var files = new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/music/single/song.flac"));
        var deletes = new RecordingDeleteMediaFiles();
        return new SecondaryHarness(source, single, albums, deletes, Service(albums, tracks, files, deletes));
    }

    private static void AssertUntouched(SecondaryHarness h, CleanupResult result)
    {
        Assert.Equal(0, result.Cleaned);
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
        Assert.True(h.Single.Monitored);
    }

    // ---- Candidate-side protection (mirror of #31) --------------------------------------

    [Theory]
    [InlineData("Live", 0, "tier2")]
    [InlineData("Live", 1, "tier2")]
    [InlineData("Live", 2, "tier2")]
    [InlineData("Remix", 0, "tier2")]
    [InlineData("Remix", 1, "tier2")]
    [InlineData("Remix", 2, "tier2")]
    [InlineData("DJ-mix", 0, "tier2")]
    [InlineData("DJ-mix", 2, "tier2")]
    [InlineData("Demo", 0, "tier2")]
    [InlineData("Demo", 1, "tier2")]
    [InlineData("Demo", 2, "tier2")]
    [InlineData("Live", 0, "tier1")]
    [InlineData("Live", 1, "tier1")]
    [InlineData("Live", 2, "tier1")]
    [InlineData("Demo", 0, "tier1")]
    [InlineData("Remix", 2, "tier1")]
    public void Cleanup_CandidateWithExcludedSecondaryType_IsNeverUnmonitoredOrDeleted(string secondaryType, int route, string evidence)
    {
        var h = SecondaryScenario(
            sourceTypes: new(), singleTypes: new() { (SecondaryAlbumType)secondaryType },
            sourceMbid: evidence == "tier1" ? "same-recording" : "studio-mbid",
            singleMbid: evidence == "tier1" ? "same-recording" : "live-or-demo-mbid",
            sourceDuration: 180000, singleDuration: evidence == "tier1" ? 180000 : 181000);

        var result = SecondaryRun(h.Service, route, h.Source, h.Single, MetadataOptions());

        AssertUntouched(h, result);
    }

    [Fact]
    public void Cleanup_CandidateWithExcludedSecondaryType_IsNeverAutoCleanedOnTier3()
    {
        var h = SecondaryScenario(new(), new() { SecondaryAlbumType.Live }, "studio-mbid", "live-mbid", 180000, 240000);

        var result = h.Service.ScanArtistWithOptions(Artist(), MetadataOptions(Tier3Action.AutoClean));

        AssertUntouched(h, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Cleanup_StudioCandidateControl_IsStillCleanedOnTier2(int route)
    {
        // Control for the protection tests above: the same scenario without the secondary
        // type is cleaned, so those tests fail only because of the candidate guard.
        var h = SecondaryScenario(new(), new(), "studio-mbid", "other-mbid", 180000, 181000);

        var result = SecondaryRun(h.Service, route, h.Source, h.Single, MetadataOptions());

        Assert.Equal(1, result.Cleaned);
        Assert.Contains((h.Single.Id, false), h.Albums.SetMonitoredCalls);
        Assert.Single(h.Deletes.DeletedFiles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Cleanup_CandidateWithUnknownSecondaryTypes_FailsClosed(int route)
    {
        var h = SecondaryScenario(new(), null, "studio-mbid", "other-mbid", 180000, 181000, setSingleTypes: true);

        var result = SecondaryRun(h.Service, route, h.Source, h.Single, MetadataOptions());

        AssertUntouched(h, result);
    }

    [Fact]
    public void Cleanup_ProtectedCandidate_LogsTheReason()
    {
        using var logger = new RecordingLogger();
        var source = Album(100, "Studio Album", "Album");
        var single = Album(200, "Song", "Single");
        single.SecondaryTypes = new() { SecondaryAlbumType.Live };
        var albums = new RecordingAlbumService(source, single);
        var tracks = new RecordingTrackService()
            .WithTracks(source.Id, Track("Song", 180000, "a", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180500, "b", fileId: 21));
        var service = Service(albums, tracks, new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/m/s.flac")), new RecordingDeleteMediaFiles(), logger);

        service.ScanArtistWithOptions(Artist(), MetadataOptions());

        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=candidate-excluded-secondary-type"));
    }

    [Fact]
    public void Cleanup_ExactAudioCleanup_NeverDeletesExcludedSecondaryTypeCandidate()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Single.SecondaryTypes = new List<SecondaryAlbumType> { SecondaryAlbumType.Live };

        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
        Assert.True(h.Single.Monitored);
    }

    [Fact]
    public void Cleanup_LegacyImportEntryPoint_NeverCleansExcludedSecondaryTypeCandidate()
    {
        var h = SecondaryScenario(new(), new() { SecondaryAlbumType.Remix }, "studio-mbid", "remix-mbid", 180000, 181000);

        h.Service.CleanupSinglesForArtist(Artist(), h.Source);

        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
    }

    // ---- Source-side: non-excluded secondary types are Tier-1 only ------------------------

    [Theory]
    [InlineData("Compilation", 0)]
    [InlineData("Compilation", 1)]
    [InlineData("Compilation", 2)]
    [InlineData("Soundtrack", 0)]
    [InlineData("Soundtrack", 2)]
    [InlineData("Spokenword", 0)]
    [InlineData("Interview", 1)]
    [InlineData("Mixtape/Street", 0)]
    [InlineData("Audio drama", 2)]
    public void Cleanup_SourceWithSecondaryType_CannotAuthorizeTier2Deletion(string secondaryType, int route)
    {
        // A re-recorded hit or radio edit on a "Greatest Hits": same bare title, length within
        // 3s, different recording MBID. Title+duration must not delete the studio single.
        var h = SecondaryScenario(new() { (SecondaryAlbumType)secondaryType }, new(), "compilation-recording", "studio-recording", 180000, 181500);

        var result = SecondaryRun(h.Service, route, h.Source, h.Single, MetadataOptions());

        AssertUntouched(h, result);
    }

    [Fact]
    public void Cleanup_SourceWithSecondaryType_CannotAuthorizeTier3AutoClean()
    {
        var h = SecondaryScenario(new() { SecondaryAlbumType.Compilation }, new(), "compilation-recording", "studio-recording", 180000, 260000);

        var result = h.Service.ScanArtistWithOptions(Artist(), MetadataOptions(Tier3Action.AutoClean));

        AssertUntouched(h, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Cleanup_SourceWithSecondaryType_StillAuthorizesTier1ExactMbid(int route)
    {
        var h = SecondaryScenario(new() { SecondaryAlbumType.Compilation }, new(), "shared-recording", "shared-recording", 180000, 200000);

        var result = SecondaryRun(h.Service, route, h.Source, h.Single, MetadataOptions());

        Assert.Equal(1, result.Cleaned);
        Assert.Contains((h.Single.Id, false), h.Albums.SetMonitoredCalls);
    }

    [Fact]
    public void Cleanup_MixedSources_StandardAlbumKeepsTier2WhileCompilationOnlyTier1()
    {
        // Track A matches a standard album by Tier 2, track B only by title+duration on a
        // compilation. The compilation cannot vouch for B, so the release is exclusive.
        var album = Album(100, "Studio Album", "Album");
        var compilation = Album(101, "Greatest Hits", "Album");
        compilation.SecondaryTypes = new() { SecondaryAlbumType.Compilation };
        var single = Album(200, "Song", "Single");
        var albums = new RecordingAlbumService(album, compilation, single);
        var tracks = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-a", fileId: 11))
            .WithTracks(compilation.Id, Track("B Side", 200000, "comp-b", fileId: 12))
            .WithTracks(single.Id, Track("Song", 180500, "single-a", fileId: 21), Track("B Side", 200500, "single-b", fileId: 22));
        var files = new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/m/a.flac"), File(22, single.Id, "/m/b.flac"));
        var deletes = new RecordingDeleteMediaFiles();
        var service = Service(albums, tracks, files, deletes);

        var result = service.ScanArtistWithOptions(Artist(), MetadataOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.KeepExclusive);
        Assert.Empty(albums.SetMonitoredCalls);
        Assert.Empty(deletes.DeletedFiles);
    }

    [Fact]
    public void Cleanup_FilelessSingleOnlyMatchedByCompilationTier2_StaysMonitored()
    {
        var source = Album(100, "Greatest Hits", "Album");
        source.SecondaryTypes = new() { SecondaryAlbumType.Compilation };
        var single = Album(200, "Song", "Single");
        var albums = new RecordingAlbumService(source, single);
        var tracks = new RecordingTrackService()
            .WithTracks(source.Id, Track("Song", 180000, "comp", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181000, "studio", fileId: 0));
        var service = Service(albums, tracks, new RecordingMediaFileService(), new RecordingDeleteMediaFiles());
        var options = new UniqueSinglesSettings().ToCleanupOptions();
        Assert.True(options.EagerUnmonitorRedundantSingles);

        var result = service.ScanArtistWithOptions(Artist(), options);

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(albums.SetMonitoredCalls);
    }

    // ---- Source-side: unknown (null) secondary types --------------------------------------

    [Fact]
    public void Cleanup_SourceWithUnknownSecondaryTypes_IsNotAComparisonSourceEvenForTier1()
    {
        var h = SecondaryScenario(null, new(), "shared-recording", "shared-recording", 180000, 180000);

        var result = h.Service.ScanArtistWithOptions(Artist(), MetadataOptions());

        AssertUntouched(h, result);
    }

    [Fact]
    public void Classify_DistinguishesUnknownFromNone()
    {
        Assert.Equal(SingleCleanupService.SecondaryTypeClass.Standard, SingleCleanupService.ClassifySecondaryTypes(new Album { SecondaryTypes = new() }));
        Assert.Equal(SingleCleanupService.SecondaryTypeClass.Standard, SingleCleanupService.ClassifySecondaryTypes(new Album { SecondaryTypes = new() { SecondaryAlbumType.Studio } }));
        Assert.Equal(SingleCleanupService.SecondaryTypeClass.Unknown, SingleCleanupService.ClassifySecondaryTypes(new Album { SecondaryTypes = null! }));
        Assert.Equal(SingleCleanupService.SecondaryTypeClass.Unknown, SingleCleanupService.ClassifySecondaryTypes(new Album { SecondaryTypes = new() { null! } }));
        Assert.Equal(SingleCleanupService.SecondaryTypeClass.Tier1Only, SingleCleanupService.ClassifySecondaryTypes(new Album { SecondaryTypes = new() { SecondaryAlbumType.Compilation } }));
        Assert.Equal(SingleCleanupService.SecondaryTypeClass.Excluded, SingleCleanupService.ClassifySecondaryTypes(new Album { SecondaryTypes = new() { SecondaryAlbumType.Compilation, SecondaryAlbumType.Live } }));
    }

    // ---- Matcher ---------------------------------------------------------------------------

    [Fact]
    public void TrackMatcher_Tier1OnlyPool_ContributesOnlyExactMbidMatches()
    {
        var single = Track("Song", 180000, "studio", fileId: 21);
        single.Id = 1;
        var titleDurationOnly = Track("Song", 180500, "compilation", fileId: 11);
        var mbidMatch = Track("Different Title", 99999, "studio", fileId: 12);

        var tier2Blocked = TrackMatcher.FindBestMatch(single, new List<Track>(), new List<Track> { titleDurationOnly }, 3000);
        var tier1Allowed = TrackMatcher.FindBestMatch(single, new List<Track>(), new List<Track> { titleDurationOnly, mbidMatch }, 3000);

        Assert.Equal(MatchTier.NoMatch, tier2Blocked.Tier);
        Assert.Equal(MatchTier.Tier1_Mbid, tier1Allowed.Tier);
        Assert.Same(mbidMatch, tier1Allowed.MatchedTrack);
    }
}
