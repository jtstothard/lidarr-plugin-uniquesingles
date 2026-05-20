using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

public class SingleCleanupServiceTests
{
    [Theory]
    [InlineData("Album")]
    [InlineData("EP")]
    public void CleanupSinglesForArtist_AlbumOrEpImport_CleansFullyRedundantMonitoredSingle(string importedType)
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Imported", importedType);
        var redundantSingle = Album(200, "Hit Single", "Single");
        var albumService = new RecordingAlbumService(importedAlbum, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Exact", 180000, "mbid-1", fileId: 11),
                Track("Duration Match", 200000, "album-mbid-2", fileId: 12))
            .WithTracks(redundantSingle.Id,
                Track("Exact", 181000, "mbid-1", fileId: 21),
                Track("Duration Match", 201000, "single-mbid-2", fileId: 22));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(900, redundantSingle.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        Assert.Equal(new[] { artist.Id }, albumService.ArtistAlbumLookupIds);
        Assert.Empty(albumService.AllLibraryLookupCalls);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.False(redundantSingle.Monitored);
        Assert.Equal(new[] { redundantSingle.Id }, mediaFileService.AlbumLookupIds);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(900, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
        Assert.Empty(trackService.ArtistLookupIds);
    }

    [Fact]
    public void CleanupSingleSelfCheck_RedundantImportedSingle_CleansItself()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var importedSingle = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(importedSingle.Id, Track("Song", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedSingle.Id, File(901, importedSingle.Id, "/music/imported-single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, importedSingle);

        Assert.Equal(new[] { artist.Id }, albumService.ArtistAlbumLookupIds);
        Assert.Contains((importedSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(importedSingle.Id, deleteMediaFiles.DeletedFiles[0].TrackFile.AlbumId);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_RedundantImportedSingle_ReturnsCleanedCounts()
    {
        var artist = Artist();
        var comparisonEp = Album(100, "EP", "EP");
        var importedSingle = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(comparisonEp, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(comparisonEp.Id, Track("Song", 180000, "ep-mbid", fileId: 11))
            .WithTracks(importedSingle.Id, Track("Song", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedSingle.Id, File(901, importedSingle.Id, "/music/imported-single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EP" }, Tier3Action.FlagOnly);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, importedSingle, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.Contains((importedSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_UnsupportedImport_ReturnsSkippedCounts()
    {
        var artist = Artist();
        var importedAlbum = Album(200, "Album", "Album");
        var albumService = new RecordingAlbumService(importedAlbum);
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, importedAlbum, options);

        Assert.Equal(0, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_Tier3Skip_DoesNotCountReviewNeeded()
    {
        var artist = Artist();
        var comparisonAlbum = Album(100, "Album", "Album");
        var importedSingle = Album(200, "Maybe Different Version", "Single");
        var logger = new RecordingLogger();
        var albumService = new RecordingAlbumService(comparisonAlbum, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(comparisonAlbum.Id, Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(importedSingle.Id, Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedSingle.Id, File(901, importedSingle.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" }, Tier3Action.Skip);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, importedSingle, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Contains(logger.InfoMessages, m => m.Contains("cleanup-skipped", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.InfoMessages, m => m.Contains("review-needed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_SingleComparison_ExcludesImportedSingleFromOwnComparisonTracks()
    {
        var artist = Artist();
        var importedSingle = Album(200, "Self Match", "Single");
        var albumService = new RecordingAlbumService(importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedSingle.Id, Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedSingle.Id, File(901, importedSingle.Id, "/music/imported-single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Single" }, Tier3Action.FlagOnly);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, importedSingle, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_UnmatchedSingle_DoesNotUnmonitorOrDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Exclusive", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Album Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Exclusive B Side", 190000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/exclusive.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_Tier3OnlySingle_LogsReviewAndDoesNotUnmonitorOrDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Maybe Different Version", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("review-needed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSinglesForArtist_UnmonitoredSingle_IsSkippedWithoutTrackLookupOrDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Already Done", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        Assert.DoesNotContain(single.Id, trackService.AlbumLookupIds);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_AlreadyUnmonitored_ReturnsSkippedCountsAndReason()
    {
        var artist = Artist();
        var comparisonAlbum = Album(100, "Album", "Album");
        var importedSingle = Album(200, "Already Done", "Single", monitored: false);
        var logger = new RecordingLogger();
        var albumService = new RecordingAlbumService(comparisonAlbum, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(comparisonAlbum.Id, Track("Song", 180000, "album-mbid", fileId: 11));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" }, Tier3Action.FlagOnly);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, importedSingle, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.DoesNotContain(importedSingle.Id, trackService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=already-unmonitored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSinglesForArtist_SingleWithNoDownloadedTracks_IsSkipped()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Not Downloaded", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 0));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_NoDownloadedTracks_ReturnsSkippedCountsAndReason()
    {
        var artist = Artist();
        var comparisonAlbum = Album(100, "Album", "Album");
        var importedSingle = Album(200, "Not Downloaded", "Single");
        var logger = new RecordingLogger();
        var albumService = new RecordingAlbumService(comparisonAlbum, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(comparisonAlbum.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(importedSingle.Id, Track("Song", 180000, "single-mbid", fileId: 0));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" }, Tier3Action.FlagOnly);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, importedSingle, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=no-downloaded-single-tracks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSinglesForArtist_UnmonitorFailure_SkipsDeleteAndDoesNotThrow()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single)
        {
            ThrowOnSetAlbumMonitored = true,
        };
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/would-delete.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var exception = Record.Exception(() => service.CleanupSinglesForArtist(artist, album));

        Assert.Null(exception);
        Assert.True(single.Monitored);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.WarnMessages, m => m.Contains("unmonitor failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSinglesForArtist_DeleteFailure_IsLoggedAndSwallowedAfterUnmonitor()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/delete-fails.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles { ThrowOnDelete = true };
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var exception = Record.Exception(() => service.CleanupSinglesForArtist(artist, album));

        Assert.Null(exception);
        Assert.False(single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.WarnMessages, m => m.Contains("delete failure", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // New methods with options
    // ============================================================

    [Fact]
    public void CleanupWithOptions_CustomDurationTolerance_AffectsMatching()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 200000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 250000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        // Default tolerance (3000ms) should NOT match (diff = 50000ms)
        var defaultOptions = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var defaultResult = service.CleanupWithOptions(artist, importedAlbum, defaultOptions);

        Assert.Equal(1, defaultResult.CandidatesChecked);
        Assert.Equal(0, defaultResult.Cleaned);
        Assert.Equal(1, defaultResult.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);

        // 50000ms tolerance should match
        var tolerantOptions = new SingleCleanupOptions(50000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var tolerantResult = service.CleanupWithOptions(artist, importedAlbum, tolerantOptions);

        Assert.Equal(1, tolerantResult.CandidatesChecked);
        Assert.Equal(1, tolerantResult.Cleaned);
        Assert.Equal(0, tolerantResult.Skipped);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupWithOptions_ReleaseTypeFiltering_OnlyComparesConfiguredTypes()
    {
        // First artist - album only
        var artist1 = Artist(1);
        var importedAlbum1 = Album(100, "Album", "Album");
        var single1 = Album(200, "Single 1", "Single");
        var albumService1 = new RecordingAlbumService(importedAlbum1, single1);
        var trackService1 = new RecordingTrackService()
            .WithTracks(importedAlbum1.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single1.Id,
                Track("Song", 181000, "single1-mbid", fileId: 21));
        var mediaFileService1 = new RecordingMediaFileService()
            .WithFiles(single1.Id, File(901, single1.Id, "/music/single1.flac"));
        var deleteMediaFiles1 = new RecordingDeleteMediaFiles();
        var service1 = Service(albumService1, trackService1, mediaFileService1, deleteMediaFiles1);

        // Only Album configured → should match because Album has track
        var albumOnlyOptions = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" }, Tier3Action.FlagOnly);
        var albumOnlyResult = service1.CleanupWithOptions(artist1, importedAlbum1, albumOnlyOptions);

        Assert.Equal(1, albumOnlyResult.Cleaned);
        Assert.Single(deleteMediaFiles1.DeletedFiles);

        // Second artist - EP only
        var artist2 = Artist(2);
        var importedEp = Album(101, "EP", "EP");
        var single2 = Album(201, "Single 2", "Single");
        var albumService2 = new RecordingAlbumService(importedEp, single2);
        var trackService2 = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Song", 180000, "ep-mbid", fileId: 12))
            .WithTracks(single2.Id,
                Track("Song", 181000, "single2-mbid", fileId: 22));
        var mediaFileService2 = new RecordingMediaFileService()
            .WithFiles(single2.Id, File(902, single2.Id, "/music/single2.flac"));
        var deleteMediaFiles2 = new RecordingDeleteMediaFiles();
        var service2 = Service(albumService2, trackService2, mediaFileService2, deleteMediaFiles2);

        // Only EP configured → should match because EP has track
        var epOnlyOptions = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EP" }, Tier3Action.FlagOnly);
        var epOnlyResult = service2.CleanupWithOptions(artist2, importedEp, epOnlyOptions);

        Assert.Equal(1, epOnlyResult.Cleaned);
        Assert.Single(deleteMediaFiles2.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_ReturnsAccurateCounts()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var redundantSingle = Album(200, "Hit", "Single");
        var unmatchedSingle = Album(201, "Exclusive", "Single");
        var albumService = new RecordingAlbumService(album, redundantSingle, unmatchedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Hit", 180000, "album-mbid", fileId: 11))
            .WithTracks(redundantSingle.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21))
            .WithTracks(unmatchedSingle.Id,
                Track("Exclusive", 190000, "single-mbid", fileId: 22));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/hit.flac"))
            .WithFiles(unmatchedSingle.Id, File(902, unmatchedSingle.Id, "/music/exclusive.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(2, result.CandidatesChecked); // 2 singles checked
        Assert.Equal(1, result.Cleaned); // 1 redundant single cleaned
        Assert.Equal(1, result.Skipped); // 1 unmatched single skipped
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_Tier3Only_ReviewNeededCounted()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var tier3Single = Album(200, "Maybe Different Version", "Single");
        var albumService = new RecordingAlbumService(album, tier3Single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(tier3Single.Id,
                Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(tier3Single.Id, File(901, tier3Single.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.ReviewNeeded);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("review-needed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanArtistWithOptions_UnmonitoredSingle_SkippedWithoutDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var unmonitoredSingle = Album(200, "Already Done", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, unmonitoredSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(unmonitoredSingle.Id,
                Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_Idempotent_SecondRunReturnsZeroCleaned()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var redundantSingle = Album(200, "Hit", "Single");
        var albumService = new RecordingAlbumService(album, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Hit", 180000, "album-mbid", fileId: 11))
            .WithTracks(redundantSingle.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/hit.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);

        // First run
        var firstResult = service.ScanArtistWithOptions(artist, options);
        Assert.Equal(1, firstResult.Cleaned);
        Assert.Equal(1, firstResult.CandidatesChecked);
        Assert.Single(deleteMediaFiles.DeletedFiles);

        // Second run - single is now unmonitored, should skip
        var secondResult = service.ScanArtistWithOptions(artist, options);
        Assert.Equal(1, secondResult.CandidatesChecked);
        Assert.Equal(0, secondResult.Cleaned);
        Assert.Equal(1, secondResult.Skipped);
        // Only one delete call (from first run)
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_UnmonitorFailure_CountsCorrectly()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single)
        {
            ThrowOnSetAlbumMonitored = true,
        };
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/would-delete.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.True(single.Monitored);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.WarnMessages, m => m.Contains("unmonitor failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanArtistWithOptions_DeleteFailure_CountsCorrectly()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/delete-fails.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles { ThrowOnDelete = true };
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned); // Unmonitored successfully
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(1, result.DeleteFailures);
        Assert.False(single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles); // Unmonitor attempt logged
        Assert.Contains(logger.WarnMessages, m => m.Contains("reason=delete-failed", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // AutoClean tests
    // ============================================================

    [Fact]
    public void ScanArtistWithOptions_AutoClean_Tier3OnlyMatch_ProceedsToCleanup()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var tier3Single = Album(200, "Maybe Different Version", "Single");
        var albumService = new RecordingAlbumService(album, tier3Single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(tier3Single.Id,
                Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(tier3Single.Id, File(901, tier3Single.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.AutoClean);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.False(tier3Single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("cleanup-approved", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanArtistWithOptions_AutoClean_NoMatchTrack_StillSkipped()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Mixed Match", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 180000, "album-mbid", fileId: 11),
                Track("Other Song", 200000, "album-mbid-2", fileId: 12))
            .WithTracks(single.Id,
                Track("Song", 220000, "single-mbid", fileId: 21),    // Tier3: title match, duration mismatch
                Track("Exclusive Track", 190000, "single-mbid-2", fileId: 22)); // NoMatch: no title match
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/mixed.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.AutoClean);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_AutoClean_UnmonitorFailure_ReturnsUnmonitorFailure()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var tier3Single = Album(200, "Maybe Different Version", "Single");
        var albumService = new RecordingAlbumService(album, tier3Single)
        {
            ThrowOnSetAlbumMonitored = true,
        };
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(tier3Single.Id,
                Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(tier3Single.Id, File(901, tier3Single.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.AutoClean);
        var result = service.ScanArtistWithOptions(artist, options);

        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.UnmonitorFailures);
        Assert.True(tier3Single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_AutoClean_FlagOnlyBehavior_UnchangedRegression()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var tier3Single = Album(200, "Maybe Different Version", "Single");
        var albumService = new RecordingAlbumService(album, tier3Single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(tier3Single.Id,
                Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(tier3Single.Id, File(901, tier3Single.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Existing FlagOnly behavior: Tier3 → review-needed, skipped, no cleanup
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.ReviewNeeded);
        Assert.True(tier3Single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("review-needed", StringComparison.OrdinalIgnoreCase));
    }

    private static SingleCleanupService Service(
        RecordingAlbumService albumService,
        RecordingTrackService trackService,
        RecordingMediaFileService mediaFileService,
        RecordingDeleteMediaFiles deleteMediaFiles,
        RecordingLogger? logger = null)
    {
        var loggerHarness = logger ?? new RecordingLogger();
        return new SingleCleanupService(albumService, trackService, mediaFileService, deleteMediaFiles, loggerHarness.Logger);
    }

    private static Artist Artist() => new() { Id = 42, Name = "Test Artist" };

    private static Artist Artist(int id) => new() { Id = id, Name = $"Test Artist {id}" };

    private static Album Album(int id, string title, string type, bool monitored = true) => new()
    {
        Id = id,
        Title = title,
        AlbumType = type,
        Monitored = monitored,
    };

    private static Track Track(string title, int duration, string mbid, int fileId) => new()
    {
        Title = title,
        Duration = duration,
        ForeignRecordingId = mbid,
        TrackFileId = fileId,
    };

    private static TrackFile File(int id, int albumId, string path) => new()
    {
        Id = id,
        AlbumId = albumId,
        Path = path,
    };

    private static T NotUsed<T>() => throw new NotSupportedException("Not used in focused cleanup tests.");

    private static void NotUsed() => throw new NotSupportedException("Not used in focused cleanup tests.");

    private sealed class RecordingAlbumService : IAlbumService
    {
        private readonly List<Album> _albums;

        public RecordingAlbumService(params Album[] albums)
        {
            _albums = albums.ToList();
        }

        public bool ThrowOnSetAlbumMonitored { get; set; }
        public List<int> ArtistAlbumLookupIds { get; } = new();
        public List<int> AllLibraryLookupCalls { get; } = new();
        public List<(int AlbumId, bool Monitored)> SetMonitoredCalls { get; } = new();

        public Album GetAlbum(int albumId)
        {
            AllLibraryLookupCalls.Add(albumId);
            return _albums.Single(a => a.Id == albumId);
        }

        public List<Album> GetAlbums(IEnumerable<int> albumIds)
        {
            var ids = albumIds.ToHashSet();
            return _albums.Where(a => ids.Contains(a.Id)).ToList();
        }

        public List<Album> GetAlbumsByArtist(int artistId)
        {
            ArtistAlbumLookupIds.Add(artistId);
            return _albums;
        }

        public List<Album> GetNextAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => NotUsed<List<Album>>();

        public List<Album> GetLastAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => NotUsed<List<Album>>();

        public List<Album> GetAlbumsByArtistMetadataId(int artistMetadataId) => NotUsed<List<Album>>();

        public List<Album> GetAlbumsForRefresh(int artistMetadataId, List<string> foreignIds) => NotUsed<List<Album>>();

        public Album AddAlbum(Album newAlbum, bool doRefresh) => NotUsed<Album>();

        public Album FindById(string foreignId) => NotUsed<Album>();

        public Album FindByTitle(int artistMetadataId, string title) => NotUsed<Album>();

        public Album FindByTitleInexact(int artistMetadataId, string title) => NotUsed<Album>();

        public List<Album> GetCandidates(int artistMetadataId, string title) => NotUsed<List<Album>>();

        public void DeleteAlbum(int albumId, bool deleteFiles, bool addImportListExclusion = false) => NotUsed();

        public List<Album> GetAllAlbums() => _albums.ToList();

        public Album UpdateAlbum(Album album) => NotUsed<Album>();

        public void SetAlbumMonitored(int albumId, bool monitored)
        {
            SetMonitoredCalls.Add((albumId, monitored));
            if (ThrowOnSetAlbumMonitored)
            {
                throw new InvalidOperationException("unmonitor failed");
            }
        }

        public void SetMonitored(IEnumerable<int> ids, bool monitored)
        {
            foreach (var id in ids)
            {
                SetAlbumMonitored(id, monitored);
            }
        }

        public void UpdateLastSearchTime(List<Album> albums) => NotUsed();

        public NzbDrone.Core.Datastore.PagingSpec<Album> AlbumsWithoutFiles(NzbDrone.Core.Datastore.PagingSpec<Album> pagingSpec) => NotUsed<NzbDrone.Core.Datastore.PagingSpec<Album>>();

        public List<Album> AlbumsBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => NotUsed<List<Album>>();

        public List<Album> ArtistAlbumsBetweenDates(Artist artist, DateTime start, DateTime end, bool includeUnmonitored) => NotUsed<List<Album>>();

        public void InsertMany(List<Album> albums) => _albums.AddRange(albums);

        public void UpdateMany(List<Album> albums) => NotUsed();

        public void DeleteMany(List<Album> albums) => NotUsed();

        public void SetAddOptions(IEnumerable<Album> albums) => NotUsed();

        public Album FindAlbumByRelease(string albumReleaseId) => NotUsed<Album>();

        public Album FindAlbumByTrackId(int trackId) => NotUsed<Album>();

        public List<Album> GetArtistAlbumsWithFiles(Artist artist)
        {
            AllLibraryLookupCalls.Add(artist.Id);
            return _albums;
        }
    }

    private sealed class RecordingTrackService : ITrackService
    {
        private readonly Dictionary<int, List<Track>> _tracksByAlbum = new();

        public List<int> AlbumLookupIds { get; } = new();
        public List<int> ArtistLookupIds { get; } = new();

        public RecordingTrackService WithTracks(int albumId, params Track[] tracks)
        {
            _tracksByAlbum[albumId] = tracks.ToList();
            return this;
        }

        public Track GetTrack(int id) => NotUsed<Track>();

        public List<Track> GetTracks(IEnumerable<int> ids) => NotUsed<List<Track>>();

        public List<Track> GetTracksByArtist(int artistId)
        {
            ArtistLookupIds.Add(artistId);
            return _tracksByAlbum.Values.SelectMany(t => t).ToList();
        }

        public List<Track> GetTracksByAlbum(int albumId)
        {
            AlbumLookupIds.Add(albumId);
            return _tracksByAlbum.TryGetValue(albumId, out var tracks) ? tracks : new List<Track>();
        }

        public List<Track> GetTracksByRelease(int albumReleaseId) => NotUsed<List<Track>>();

        public List<Track> GetTracksByReleases(List<int> albumReleaseIds) => NotUsed<List<Track>>();

        public List<Track> GetTracksForRefresh(int albumReleaseId, List<string> foreignTrackIds) => NotUsed<List<Track>>();

        public List<Track> TracksWithFiles(int artistId) => _tracksByAlbum.Values.SelectMany(t => t).Where(t => t.HasFile).ToList();

        public List<Track> TracksWithoutFiles(int albumId) => GetTracksByAlbum(albumId).Where(t => !t.HasFile).ToList();

        public List<Track> GetTracksByFileId(int trackFileId) => _tracksByAlbum.Values.SelectMany(t => t).Where(t => t.TrackFileId == trackFileId).ToList();

        public List<Track> GetTracksByFileId(IEnumerable<int> trackFileIds)
        {
            var ids = trackFileIds.ToHashSet();
            return _tracksByAlbum.Values.SelectMany(t => t).Where(t => ids.Contains(t.TrackFileId)).ToList();
        }

        public void UpdateTrack(Track track) => NotUsed();

        public void InsertMany(List<Track> tracks) => NotUsed();

        public void UpdateMany(List<Track> tracks) => NotUsed();

        public void DeleteMany(List<Track> tracks) => NotUsed();

        public void SetFileIds(List<Track> tracks) => NotUsed();
    }

    private sealed class RecordingMediaFileService : IMediaFileService
    {
        private readonly Dictionary<int, List<TrackFile>> _filesByAlbum = new();

        public List<int> AlbumLookupIds { get; } = new();

        public RecordingMediaFileService WithFiles(int albumId, params TrackFile[] files)
        {
            _filesByAlbum[albumId] = files.ToList();
            return this;
        }

        public TrackFile Add(TrackFile trackFile)
        {
            WithFiles(trackFile.AlbumId, GetFilesByAlbum(trackFile.AlbumId).Append(trackFile).ToArray());
            return trackFile;
        }

        public void AddMany(List<TrackFile> trackFiles)
        {
            foreach (var trackFile in trackFiles)
            {
                Add(trackFile);
            }
        }

        public void Update(TrackFile trackFile) => NotUsed();

        public void Update(List<TrackFile> trackFile) => NotUsed();

        public void Delete(TrackFile trackFile, DeleteMediaFileReason reason)
        {
            if (_filesByAlbum.TryGetValue(trackFile.AlbumId, out var files))
            {
                files.RemoveAll(f => f.Id == trackFile.Id);
            }
        }

        public void DeleteMany(List<TrackFile> trackFiles, DeleteMediaFileReason reason)
        {
            foreach (var trackFile in trackFiles)
            {
                Delete(trackFile, reason);
            }
        }

        public List<TrackFile> GetFilesByArtist(int artistId) => NotUsed<List<TrackFile>>();

        public List<TrackFile> GetFilesByAlbum(int albumId)
        {
            AlbumLookupIds.Add(albumId);
            return _filesByAlbum.TryGetValue(albumId, out var files) ? files : new List<TrackFile>();
        }

        public List<TrackFile> GetFilesByRelease(int releaseId) => NotUsed<List<TrackFile>>();

        public List<TrackFile> GetUnmappedFiles() => new();

        public List<System.IO.Abstractions.IFileInfo> FilterUnchangedFiles(List<System.IO.Abstractions.IFileInfo> files, FilterFilesType filter) => files;

        public TrackFile Get(int id)
        {
            return _filesByAlbum.Values.SelectMany(f => f).Single(f => f.Id == id);
        }

        public List<TrackFile> Get(IEnumerable<int> ids)
        {
            var requested = ids.ToHashSet();
            return _filesByAlbum.Values.SelectMany(f => f).Where(f => requested.Contains(f.Id)).ToList();
        }

        public List<TrackFile> GetFilesWithBasePath(string path)
        {
            return _filesByAlbum.Values.SelectMany(f => f)
                .Where(f => f.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public List<TrackFile> GetFileWithPath(List<string> path)
        {
            var requested = path.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _filesByAlbum.Values.SelectMany(f => f).Where(f => requested.Contains(f.Path)).ToList();
        }

        public TrackFile GetFileWithPath(string path)
        {
            return _filesByAlbum.Values.SelectMany(f => f).Single(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        public void UpdateMediaInfo(List<TrackFile> trackFiles) => NotUsed();
    }

    private sealed class RecordingDeleteMediaFiles : IDeleteMediaFiles
    {
        public bool ThrowOnDelete { get; set; }
        public List<(Artist Artist, TrackFile TrackFile)> DeletedFiles { get; } = new();

        public void DeleteTrackFile(Artist artist, TrackFile trackFile)
        {
            DeletedFiles.Add((artist, trackFile));
            if (ThrowOnDelete)
            {
                throw new InvalidOperationException("delete failed");
            }
        }

        public void DeleteTrackFile(TrackFile trackFile, string subfolder = "") => NotUsed();
    }

    private sealed class RecordingLogger : IDisposable
    {
        private readonly LogFactory _factory;
        private readonly MemoryTarget _target;

        public RecordingLogger()
        {
            _factory = new LogFactory();
            _target = new MemoryTarget
            {
                Layout = "${level}|${message}|${exception:format=Type,Message}",
            };

            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(_target);
            _factory.Configuration = config;
            Logger = _factory.GetLogger(Guid.NewGuid().ToString("N"));
        }

        public Logger Logger { get; }

        public List<string> InfoMessages => Entries.Where(entry => entry.StartsWith("Info|", StringComparison.Ordinal)).ToList();

        public List<string> WarnMessages => Entries.Where(entry => entry.StartsWith("Warn|", StringComparison.Ordinal)).ToList();

        private IList<string> Entries
        {
            get
            {
                _factory.Flush();
                return _target.Logs;
            }
        }

        public void Dispose()
        {
            _factory.Flush();
            _factory.Dispose();
        }
    }
}
