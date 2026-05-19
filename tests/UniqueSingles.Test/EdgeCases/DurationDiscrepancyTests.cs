using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC11: Duration Discrepancies Between Sources
///
/// Edge case: Same recording has slightly different duration values (mastering differences,
/// encoding variations, etc.). Example: Album track 185000ms, Single track 184000ms.
///
/// From EDGE-CASES.md:
/// "Rule: ±3 seconds for 'same recording' confidence. Larger differences should NOT auto-match.
///
/// Duration tolerance is important. ±3 seconds is reasonable.
///
/// But: Some tracks may have larger differences:
/// - Radio edit vs album version: Could differ by 30-60+ seconds
/// - Different mastering: Usually within ±5 seconds"
///
/// This tests that the ±3s tolerance works correctly.
/// </summary>
public class DurationDiscrepancyTests
{
    [Fact]
    public void CleanupSinglesForArtist_DurationWithinTolerance_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, 1 second difference (within ±3s tolerance)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 185000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 184000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (duration within ±3s)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_DurationAtToleranceBoundary_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, exactly 3 second difference (at tolerance boundary)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 183000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (duration at ±3s boundary)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_DurationOutsideTolerance_NoMatch()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Different recordings, 4 second difference (outside ±3s tolerance)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 184000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be kept (duration outside ±3s)
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_LargeDurationDifference_NoMatch()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Different recordings, 30 second difference (likely different version)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 210000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be kept (large duration difference = different version)
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheck_DurationWithinTolerance_Matches()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // 2 second difference
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Test", 181000, "rec-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Test", 183000, "rec-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/test.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, single);

        // Should match within tolerance
        Assert.False(single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_CustomTolerance_UsesConfiguredValue()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var singleA = Album(200, "Single A", "Single"); // 2s diff - matches with 3s tolerance
        var singleB = Album(201, "Single B", "Single"); // 4s diff - no match with 3s tolerance

        var albumService = new RecordingAlbumService(singleA, singleB, album);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("Track", 180000, "track-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("Track", 186000, "track-mbid-2", fileId: 31))
            .WithTracks(album.Id,
                Track("Track", 182000, "track-mbid", fileId: 11));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/track.flac"))
            .WithFiles(singleB.Id, File(901, singleB.Id, "/music/track.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        // Use 3000ms (±3s) tolerance
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // SingleA cleaned (2s diff within tolerance), SingleB kept (4s diff outside)
        Assert.Equal(2, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.False(singleA.Monitored);
        Assert.True(singleB.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_DurationExactlySame_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, identical duration
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 180000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (exact duration match)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_NegativeDurationDifference_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, single is shorter (negative difference)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 185000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 183000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (2s difference within tolerance, regardless of direction)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
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

        public bool ThrowOnSetAlbumMonitored { get; set; } = false;
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
        public bool ThrowOnDelete { get; set; } = false;
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