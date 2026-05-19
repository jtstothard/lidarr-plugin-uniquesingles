using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC9: Multi-Artist / Featuring Tracks
///
/// Edge case: A track featuring another artist might have different title formatting:
/// - Album: "Parting Gift" (feat. Brendan Kelly)
/// - Single: "Parting Gift"
///
/// From EDGE-CASES.md:
/// "Mitigation: Title matching should strip featuring annotations for comparison."
///
/// The implementation uses TitleNormalizer which handles stripping featuring annotations
/// as part of the normalization process.
/// </summary>
public class FeaturingAnnotationTests
{
    [Fact]
    public void CleanupSinglesForArtist_AlbumHasFeaturingSingleDoesNot_MatchesAfterNormalization()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, album has (feat. ...) suffix
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Parting Gift (feat. Brendan Kelly)", 180000, "recording-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Parting Gift", 181000, "recording-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/parting-gift.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (matches after stripping featuring annotation)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(900, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
    }

    [Fact]
    public void CleanupSinglesForArtist_SingleHasFeaturingAlbumDoesNot_MatchesAfterNormalization()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, single has (feat. ...) suffix
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Track Name", 180000, "recording-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Track Name (feat. Another Artist)", 181000, "recording-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/track.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (matches after stripping featuring annotation)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_DifferentFeaturingArtists_DoesNotMatch()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Different recordings (both have featuring but different artists)
        // Durations differ by 10s - outside ±3s tolerance so no Tier 2 match
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song (feat. Artist A)", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song (feat. Artist B)", 190000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be kept (different featured artists + duration outside tolerance)
        // After normalization both become "song", but 10s diff > 3s tolerance = no Tier 2 match
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheck_FeaturingInAlbum_Matches()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Test (featuring Guest)", 180000, "rec-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Test", 181000, "rec-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/test.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, single);

        // Should match after normalization
        Assert.False(single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_VariousFeaturingFormats_MatchesCorrectly()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var singleA = Album(200, "Single A", "Single"); // Same recording
        var singleB = Album(201, "Single B", "Single"); // Different recording

        var albumService = new RecordingAlbumService(singleA, singleB, album);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("Collab", 180000, "collab-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("Different", 185000, "different-mbid", fileId: 31))
            .WithTracks(album.Id,
                Track("Collab (feat. Partner)", 181000, "collab-mbid", fileId: 11),
                Track("Other", 190000, "other-mbid", fileId: 12));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/collab.flac"))
            .WithFiles(singleB.Id, File(901, singleB.Id, "/music/different.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // SingleA cleaned (matches after stripping feat), SingleB kept (no match)
        Assert.Equal(2, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.False(singleA.Monitored);
        Assert.True(singleB.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_FeaturingWithParenthesesAndBrackets_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Test various featuring annotation formats
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song [feat. X]", 180000, "rec-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song (featuring Y)", 181000, "rec-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Should match (both formats stripped)
        Assert.False(single.Monitored);
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