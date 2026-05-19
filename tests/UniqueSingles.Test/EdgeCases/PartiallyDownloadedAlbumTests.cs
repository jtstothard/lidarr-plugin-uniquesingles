using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC7: Partially Downloaded Albums
///
/// Edge case: An album has more tracks than downloaded files. Some tracks are present,
/// others are not. Only tracks with hasFile=true should be considered for matching.
///
/// From EDGE-CASES.md:
/// "Risk: Single track might not actually be in the library even though the album entry exists.
///  Mitigation: Check individual track's hasFile field."
///
/// "Rule: Only consider a track as 'on the album' if hasFile == true for that specific track."
/// </summary>
public class PartiallyDownloadedAlbumTests
{
    [Fact]
    public void CleanupSinglesForArtist_PartiallyDownloadedAlbum_OnlyHasFileTracksMatched()
    {
        var artist = Artist();
        var partiallyDownloadedAlbum = Album(100, "Partial Album", "Album");
        var single = Album(200, "Hit Single", "Single");

        // Album has 14 tracks but only 10 imported (4 tracks have no files)
        var albumService = new RecordingAlbumService(partiallyDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(partiallyDownloadedAlbum.Id,
                Track("Hit", 180000, "album-mbid", fileId: 11),       // Has file
                Track("Missing", 185000, "missing-mbid", fileId: 0),   // No file
                Track("Also Missing", 190000, "also-missing-mbid", fileId: 0)) // No file
            .WithTracks(single.Id,
                Track("Hit", 181000, "single-mbid", fileId: 21),
                Track("Missing", 186000, "single-missing-mbid", fileId: 22),
                Track("Also Missing", 191000, "single-also-missing-mbid", fileId: 23));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id,
                File(900, single.Id, "/music/hit.flac"),
                File(901, single.Id, "/music/missing.flac"),
                File(902, single.Id, "/music/also-missing.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, partiallyDownloadedAlbum);

        // Only "Hit" single track should be cleaned (album has it)
        // "Missing" and "Also Missing" tracks on single should be kept (album doesn't have them)
        Assert.True(single.Monitored); // Single kept because it has tracks not on album
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_AllSingleTracksOnAlbumWithFiles_SingleIsCleaned()
    {
        var artist = Artist();
        var partiallyDownloadedAlbum = Album(100, "Partial Album", "Album");
        var single = Album(200, "Triple Single", "Single");

        // Album has all the tracks the single has (even if album has other missing tracks)
        var albumService = new RecordingAlbumService(partiallyDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(partiallyDownloadedAlbum.Id,
                Track("Track A", 180000, "album-mbid-a", fileId: 11),
                Track("Track B", 185000, "album-mbid-b", fileId: 12),
                Track("Track C", 190000, "album-mbid-c", fileId: 13),
                Track("Missing", 200000, "missing-mbid", fileId: 0)) // Album has missing track
            .WithTracks(single.Id,
                Track("Track A", 181000, "single-mbid-a", fileId: 21),
                Track("Track B", 186000, "single-mbid-b", fileId: 22),
                Track("Track C", 191000, "single-mbid-c", fileId: 23));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id,
                File(900, single.Id, "/music/track-a.flac"),
                File(901, single.Id, "/music/track-b.flac"),
                File(902, single.Id, "/music/track-c.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, partiallyDownloadedAlbum);

        // Single SHOULD be cleaned (all its tracks are on album with files)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Equal(3, deleteMediaFiles.DeletedFiles.Count);
    }

    [Fact]
    public void CleanupSingleSelfCheck_PartiallyDownloadedAlbum_TracksWithoutFileIgnored()
    {
        var artist = Artist();
        var partiallyDownloadedAlbum = Album(100, "Partial Album", "Album");
        var single = Album(200, "Single", "Single");

        var albumService = new RecordingAlbumService(partiallyDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(partiallyDownloadedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 0), // No file on album
                Track("Other", 185000, "other-mbid", fileId: 0))
            .WithTracks(single.Id,
                Track("Song", 181000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, single);

        // Single should be kept (album track has no file)
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_PartiallyDownloadedAlbum_OnlyHasFileTracksConsidered()
    {
        var artist = Artist();
        var partiallyDownloadedAlbum = Album(100, "Partial Album", "Album");
        var single = Album(200, "Single", "Single");

        var albumService = new RecordingAlbumService(partiallyDownloadedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(partiallyDownloadedAlbum.Id,
                Track("Has File", 180000, "has-file-mbid", fileId: 11),
                Track("No File", 185000, "no-file-mbid", fileId: 0))
            .WithTracks(single.Id,
                Track("Has File", 181000, "single-has-file-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/has-file.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single should be cleaned (its track exists on album with file)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.False(single.Monitored);
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