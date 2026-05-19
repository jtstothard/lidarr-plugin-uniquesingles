using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC6: Album Not Yet Downloaded
///
/// Edge case: An album is monitored but has no downloaded files (trackFileCount == 0).
/// Expected behavior: Single is kept (not unmonitored or deleted) because the album
/// doesn't actually have the track in the library yet.
///
/// From EDGE-CASES.md:
/// "Rule: User specified 'only albums in library downloaded/imported should be considered.'
///  Implementation: Check statistics.trackFileCount > 0 before using an album/EP for matching."
///
/// This means a single won't be flagged as redundant until the album actually downloads.
/// Good — avoids premature deletion.
/// </summary>
public class AlbumNotDownloadedTests
{
    [Fact]
    public void CleanupSinglesForArtist_AlbumNotDownloaded_SingleIsKept()
    {
        var artist = Artist();
        var notDownloadedAlbum = Album(100, "Not Downloaded", "Album");
        var single = Album(200, "Hit Single", "Single");

        var albumService = new RecordingAlbumService(notDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(notDownloadedAlbum.Id,
                Track("Hit", 180000, "album-mbid", fileId: 0))  // No file (album not downloaded)
            .WithTracks(single.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21));  // Single has file

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/hit.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, notDownloadedAlbum);

        // Single should NOT be unmonitored (album has no files)
        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_AlbumHasNoTrackFileServiceCalls_SingleIsKept()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 180000, "album-mbid", fileId: 0))  // No file
            .WithTracks(single.Id,
                Track("Song", 181000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/single.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        // Single should be kept
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        // MediaFileService should NOT have been called for the album (no files to look up)
        Assert.Empty(mediaFileService.AlbumLookupIds.Where(id => id == album.Id));
    }

    [Fact]
    public void CleanupSingleSelfCheck_AlbumNotDownloaded_SingleIsKept()
    {
        var artist = Artist();
        var notDownloadedAlbum = Album(100, "Not Downloaded", "Album");
        var single = Album(200, "Hit Single", "Single");

        var albumService = new RecordingAlbumService(notDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(notDownloadedAlbum.Id,
                Track("Hit", 180000, "album-mbid", fileId: 0))
            .WithTracks(single.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/hit.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, single);

        // Single should be kept
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_AlbumNotDownloaded_SingleIsKept()
    {
        var artist = Artist();
        var notDownloadedAlbum = Album(100, "Not Downloaded", "Album");
        var single = Album(200, "Hit Single", "Single");

        var albumService = new RecordingAlbumService(notDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(notDownloadedAlbum.Id,
                Track("Hit", 180000, "album-mbid", fileId: 0))
            .WithTracks(single.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/hit.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single checked but skipped (album has no files)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.True(single.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_AlbumDownloadedLater_SingleIsCleaned()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Hit Single", "Single");

        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Hit", 180000, "album-mbid", fileId: 11))  // Album HAS file now
            .WithTracks(single.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(album.Id, File(800, album.Id, "/music/album-hit.flac"))
            .WithFiles(single.Id, File(900, single.Id, "/music/single-hit.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        // Single SHOULD be unmonitored and deleted (album now has the file)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(900, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
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

    private static Artist Artist() => new() { Id = 42, Name = "Chappell Roan" };

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