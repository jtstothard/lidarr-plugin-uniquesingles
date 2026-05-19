using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC1: Multi-Track Singles with Exclusive B-Sides
///
/// Edge case: A single has multiple tracks, and not all tracks appear on albums/EPs.
/// Expected behavior: Single is kept (not unmonitored or deleted) because it has exclusive content.
///
/// From EDGE-CASES.md:
/// "Only mark single as redundant if ALL its tracks appear on monitored albums/EPs."
///
/// Example: "Bitter" single (ID 8185) — 2 tracks:
/// - Track 1: "Bitter" (recording 51bd7a30) — NOT on any album → unique
/// - Track 2: "Die Young (acoustic)" (recording 60ca4db8) — NOT on any album → unique
///
/// If we check track 1 ("Bitter") and it WAS on an album:
/// - Wrong to delete the whole single — track 2 is exclusive
/// - Must check ALL tracks on the single before deciding
/// </summary>
public class MultiTrackSingleTests
{
    [Fact]
    public void CleanupSinglesForArtist_MultiTrackSingleWithExclusiveBSide_SingleIsKept()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Main Album", "Album");
        var multiTrackSingle = Album(200, "Bitter", "Single");

        // Album has track 1 only ("Bitter")
        var albumService = new RecordingAlbumService(importedAlbum, multiTrackSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Bitter", 180000, "album-mbid-1", fileId: 11))
            .WithTracks(multiTrackSingle.Id,
                Track("Bitter", 181000, "single-mbid-1", fileId: 21),  // Matches album
                Track("Die Young (acoustic)", 190000, "single-mbid-2", fileId: 22));  // Exclusive B-side

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(multiTrackSingle.Id,
                File(900, multiTrackSingle.Id, "/music/bitter.flac"),
                File(901, multiTrackSingle.Id, "/music/die-young-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should NOT be unmonitored (it has exclusive B-side)
        Assert.True(multiTrackSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_MultiTrackSingleWithAllTracksOnAlbum_SingleIsCleaned()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Main Album", "Album");
        var multiTrackSingle = Album(200, "Double Track", "Single");

        // Album has both tracks from the single
        var albumService = new RecordingAlbumService(importedAlbum, multiTrackSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Track One", 180000, "album-mbid-1", fileId: 11),
                Track("Track Two", 190000, "album-mbid-2", fileId: 12))
            .WithTracks(multiTrackSingle.Id,
                Track("Track One", 181000, "single-mbid-1", fileId: 21),
                Track("Track Two", 191000, "single-mbid-2", fileId: 22));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(multiTrackSingle.Id,
                File(900, multiTrackSingle.Id, "/music/track-one.flac"),
                File(901, multiTrackSingle.Id, "/music/track-two.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single SHOULD be unmonitored and deleted (all tracks are on album)
        Assert.False(multiTrackSingle.Monitored);
        Assert.Contains((multiTrackSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Equal(2, deleteMediaFiles.DeletedFiles.Count);
    }

    [Fact]
    public void CleanupSingleSelfCheck_MultiTrackSingleWithExclusiveBSide_SingleIsKept()
    {
        var artist = Artist();
        var album = Album(100, "Main Album", "Album");
        var multiTrackSingle = Album(200, "The Subway / The Giver", "Single");

        // Album has track 1 only
        var albumService = new RecordingAlbumService(album, multiTrackSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("The Subway", 180000, "album-mbid-1", fileId: 11))
            .WithTracks(multiTrackSingle.Id,
                Track("The Subway", 181000, "single-mbid-1", fileId: 21),
                Track("The Giver", 190000, "single-mbid-2", fileId: 22));  // Exclusive

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(multiTrackSingle.Id,
                File(900, multiTrackSingle.Id, "/music/the-subway.flac"),
                File(901, multiTrackSingle.Id, "/music/the-giver.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, multiTrackSingle);

        // Single should NOT be unmonitored (has exclusive B-side)
        Assert.True(multiTrackSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_MultiTrackSingleWithExclusiveBSide_SingleIsKept()
    {
        var artist = Artist();
        var album = Album(100, "Main Album", "Album");
        var multiTrackSingle = Album(200, "Bitter", "Single");

        var albumService = new RecordingAlbumService(album, multiTrackSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Bitter", 180000, "album-mbid", fileId: 11))
            .WithTracks(multiTrackSingle.Id,
                Track("Bitter", 181000, "single-mbid-1", fileId: 21),
                Track("Die Young (acoustic)", 190000, "single-mbid-2", fileId: 22));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(multiTrackSingle.Id,
                File(900, multiTrackSingle.Id, "/music/bitter.flac"),
                File(901, multiTrackSingle.Id, "/music/die-young-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single checked but skipped (has exclusive content)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.True(multiTrackSingle.Monitored);
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
        HasFile = fileId > 0,
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