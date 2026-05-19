using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
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
        Assert.Contains(logger.WarnMessages, m => m.Contains("delete failure", StringComparison.OrdinalIgnoreCase));
    }

    private static SingleCleanupService Service(
        RecordingAlbumService albumService,
        RecordingTrackService trackService,
        RecordingMediaFileService mediaFileService,
        RecordingDeleteMediaFiles deleteMediaFiles,
        RecordingLogger? logger = null)
    {
        return new SingleCleanupService(albumService, trackService, mediaFileService, deleteMediaFiles, logger ?? new RecordingLogger());
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

        public List<Album> GetAlbumsByArtist(int artistId)
        {
            ArtistAlbumLookupIds.Add(artistId);
            return _albums;
        }

        public Album GetAlbum(int albumId)
        {
            AllLibraryLookupCalls.Add(albumId);
            return _albums.Single(a => a.Id == albumId);
        }

        public List<Album> GetArtistAlbumsWithFiles(Artist artist)
        {
            AllLibraryLookupCalls.Add(artist.Id);
            return _albums;
        }

        public void SetAlbumMonitored(int albumId, bool monitored)
        {
            SetMonitoredCalls.Add((albumId, monitored));
            if (ThrowOnSetAlbumMonitored)
            {
                throw new InvalidOperationException("unmonitor failed");
            }
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

        public List<Track> GetTracksByAlbum(int albumId)
        {
            AlbumLookupIds.Add(albumId);
            return _tracksByAlbum.TryGetValue(albumId, out var tracks) ? tracks : new List<Track>();
        }

        public List<Track> GetTracksByArtist(int artistId)
        {
            ArtistLookupIds.Add(artistId);
            return _tracksByAlbum.Values.SelectMany(t => t).ToList();
        }
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

        public TrackFile Get(int id)
        {
            return _filesByAlbum.Values.SelectMany(f => f).Single(f => f.Id == id);
        }

        public List<TrackFile> GetFilesByAlbum(int albumId)
        {
            AlbumLookupIds.Add(albumId);
            return _filesByAlbum.TryGetValue(albumId, out var files) ? files : new List<TrackFile>();
        }
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
    }

    private sealed class RecordingLogger : Logger
    {
        public List<string> InfoMessages { get; } = new();
        public List<string> WarnMessages { get; } = new();

        public override void Info(string message, params object?[] args)
        {
            InfoMessages.Add(WithArgs(message, args));
        }

        public override void Warn(Exception exception, string message, params object?[] args)
        {
            WarnMessages.Add(WithArgs(message, args));
        }

        private static string WithArgs(string message, object?[] args)
        {
            return args.Length == 0 ? message : $"{message} {string.Join(" ", args.Select(a => a?.ToString()))}";
        }
    }
}
