using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC8: Duplicate Singles (Same Track, Multiple Single Releases)
///
/// Edge case: The same track appears on multiple single releases for the same artist.
/// Expected behavior: Each single is checked independently against albums/EPs.
///
/// From EDGE-CASES.md:
/// "Example: 'The Giver' appears on:
///  - Standalone single 'The Giver' (ID 10294) — recording 57887697
///  - 2-track single 'The Subway / The Giver' (ID 232572) — recording 57887697 (SAME)
///
/// Both are singles. We only check against albums/EPs, so neither would be flagged as redundant
/// by the album check.
///
/// But: If 'The Giver' later appears on a monitored album, BOTH singles would be flagged.
/// The script should handle this correctly — it unmonitors each single independently."
/// </summary>
public class DuplicateSinglesTests
{
    [Fact]
    public void CleanupSinglesForArtist_DuplicateSinglesWithoutAlbum_BothKept()
    {
        var artist = Artist();
        var singleA = Album(200, "The Giver", "Single");
        var singleB = Album(201, "The Subway / The Giver", "Single");
        var importedAlbum = Album(100, "Main Album", "Album");

        // Both singles have the same track, but album doesn't have it
        var albumService = new RecordingAlbumService(singleA, singleB, importedAlbum);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("The Giver", 180000, "giver-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("The Subway", 170000, "subway-mbid", fileId: 31),
                Track("The Giver", 180000, "giver-mbid", fileId: 32))
            .WithTracks(importedAlbum.Id,
                Track("Other Song", 190000, "other-mbid", fileId: 11));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/giver.flac"))
            .WithFiles(singleB.Id,
                File(901, singleB.Id, "/music/subway.flac"),
                File(902, singleB.Id, "/music/giver.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Both singles should be kept (album doesn't have the tracks)
        Assert.True(singleA.Monitored);
        Assert.True(singleB.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_DuplicateSinglesWithAlbum_BothCleaned()
    {
        var artist = Artist();
        var singleA = Album(200, "The Giver", "Single");
        var singleB = Album(201, "The Subway / The Giver", "Single");
        var importedAlbum = Album(100, "Main Album", "Album");

        // Album now has "The Giver" track (but not "The Subway")
        var albumService = new RecordingAlbumService(singleA, singleB, importedAlbum);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("The Giver", 180000, "giver-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("The Subway", 170000, "subway-mbid", fileId: 31),
                Track("The Giver", 180000, "giver-mbid", fileId: 32))
            .WithTracks(importedAlbum.Id,
                Track("The Giver", 181000, "giver-mbid", fileId: 11));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/giver.flac"))
            .WithFiles(singleB.Id,
                File(901, singleB.Id, "/music/subway.flac"),
                File(902, singleB.Id, "/music/giver.flac"))
            .WithFiles(importedAlbum.Id, File(800, importedAlbum.Id, "/music/giver.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // SingleA should be cleaned (all tracks on album)
        // SingleB should be kept (has exclusive "The Subway" track)
        Assert.False(singleA.Monitored);
        Assert.True(singleB.Monitored); // Kept because of exclusive B-side
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(900, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
    }

    [Fact]
    public void CleanupSingleSelfCheck_DuplicateSingles_EachCheckedIndependently()
    {
        var artist = Artist();
        var singleA = Album(200, "Standalone Single", "Single");
        var singleB = Album(201, "Multi-Track Single", "Single");
        var album = Album(100, "Album", "Album");

        var albumService = new RecordingAlbumService(singleA, singleB, album);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("Same Song", 180000, "same-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("Same Song", 180000, "same-mbid", fileId: 31),
                Track("Exclusive", 190000, "exclusive-mbid", fileId: 32))
            .WithTracks(album.Id,
                Track("Same Song", 181000, "same-mbid", fileId: 11));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/same.flac"))
            .WithFiles(singleB.Id,
                File(901, singleB.Id, "/music/same.flac"),
                File(902, singleB.Id, "/music/exclusive.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        // Check singleA independently
        service.CleanupSingleSelfCheck(artist, singleA);
        Assert.False(singleA.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        deleteMediaFiles.DeletedFiles.Clear();

        // Check singleB independently (should be kept due to exclusive track)
        service.CleanupSingleSelfCheck(artist, singleB);
        Assert.True(singleB.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_DuplicateSingles_AllChecked()
    {
        var artist = Artist();
        var singleA = Album(200, "Single A", "Single");
        var singleB = Album(201, "Single B", "Single");
        var album = Album(100, "Album", "Album");

        var albumService = new RecordingAlbumService(singleA, singleB, album);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("Track", 180000, "track-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("Track", 180000, "track-mbid", fileId: 31))
            .WithTracks(album.Id,
                Track("Track", 181000, "track-mbid", fileId: 11));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/track.flac"))
            .WithFiles(singleB.Id, File(901, singleB.Id, "/music/track.flac"))
            .WithFiles(album.Id, File(800, album.Id, "/music/track.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Both singles checked and cleaned
        Assert.Equal(2, result.CandidatesChecked);
        Assert.Equal(2, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2, deleteMediaFiles.DeletedFiles.Count);
        Assert.False(singleA.Monitored);
        Assert.False(singleB.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_DuplicateSinglesSameRecordingAllCleaned()
    {
        var artist = Artist();
        var singleA = Album(200, "Duplicate Single 1", "Single");
        var singleB = Album(201, "Duplicate Single 2", "Single");
        var singleC = Album(202, "Duplicate Single 3", "Single");
        var album = Album(100, "Album", "Album");

        // Three singles with same single track, all on album
        var albumService = new RecordingAlbumService(singleA, singleB, singleC, album);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("Hit", 180000, "hit-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("Hit", 180000, "hit-mbid", fileId: 31))
            .WithTracks(singleC.Id,
                Track("Hit", 180000, "hit-mbid", fileId: 41))
            .WithTracks(album.Id,
                Track("Hit", 181000, "hit-mbid", fileId: 11));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/hit.flac"))
            .WithFiles(singleB.Id, File(901, singleB.Id, "/music/hit.flac"))
            .WithFiles(singleC.Id, File(902, singleC.Id, "/music/hit.flac"))
            .WithFiles(album.Id, File(800, album.Id, "/music/hit.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        // All three singles should be cleaned independently
        Assert.False(singleA.Monitored);
        Assert.False(singleB.Monitored);
        Assert.False(singleC.Monitored);
        Assert.Equal(3, deleteMediaFiles.DeletedFiles.Count);
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