using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC10: Case and Punctuation Differences
///
/// Edge case: Same song appears with different casing and punctuation across releases.
/// Example: Album has "HOT TO GO!" (all caps + exclamation), single might have "Hot to Go!"
///
/// From EDGE-CASES.md:
/// "Mitigation: Case-insensitive title matching + normalize punctuation:
///  - Remove extra spaces, punctuation variations
///  - Strip trailing punctuation
///  - Normalize spaces"
///
/// The implementation uses TitleNormalizer which handles case normalization,
/// punctuation removal, and space normalization.
/// </summary>
public class CasePunctuationTests
{
    [Fact]
    public void CleanupSinglesForArtist_CaseDifference_MatchesAfterNormalization()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, different case
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("HOT TO GO!", 180000, "recording-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Hot to Go!", 181000, "recording-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/hot-to-go.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (case normalized)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_PunctuationDifference_MatchesAfterNormalization()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, different punctuation
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song!", 180000, "recording-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Song", 181000, "recording-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/song.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (punctuation normalized)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_CaseAndPunctuationCombined_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, different case and punctuation
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("YES!!!", 180000, "recording-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("yes", 181000, "recording-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/yes.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (both case and punctuation normalized)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_ExtraSpaces_Matches()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Same recording, different spacing
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Track Name", 180000, "recording-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Track  Name", 181000, "recording-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/track.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be cleaned (spaces normalized)
        Assert.False(single.Monitored);
        Assert.Contains((single.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheck_VariousPunctuationStyles_Matches()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Test: album has multiple exclamation marks, single has none
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Wow!!!", 180000, "rec-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("wow", 181000, "rec-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/wow.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, single);

        // Should match after normalization
        Assert.False(single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_MixedCasePunctuation_DifferentResults()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var singleA = Album(200, "Single A", "Single"); // Match (case/punc diff)
        var singleB = Album(201, "Single B", "Single"); // No match (different title)

        var albumService = new RecordingAlbumService(singleA, singleB, album);
        var trackService = new RecordingTrackService()
            .WithTracks(singleA.Id,
                Track("song", 180000, "song-mbid", fileId: 21))
            .WithTracks(singleB.Id,
                Track("different", 185000, "different-mbid", fileId: 31))
            .WithTracks(album.Id,
                Track("SONG???", 181000, "song-mbid", fileId: 11),
                Track("Other", 190000, "other-mbid", fileId: 12));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleA.Id, File(900, singleA.Id, "/music/song.flac"))
            .WithFiles(singleB.Id, File(901, singleB.Id, "/music/different.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // SingleA cleaned (matches after case/punc normalization), SingleB kept
        Assert.Equal(2, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.False(singleA.Monitored);
        Assert.True(singleB.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_DifferentPunctuationInMiddle_NoMatch()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");

        // Different recordings (hyphen vs space - not just trailing punctuation)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Track-Name", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Track Name", 181000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/track.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should be kept (different punctuation in middle = different title)
        Assert.True(single.Monitored);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
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