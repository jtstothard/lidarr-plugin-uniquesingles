using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC4: Same Track Name, Different Recordings (Acoustic/Live)
///
/// Edge case: Album and single have tracks with the same name but are different
/// recordings (acoustic vs original, live vs studio). Different MBID, different title
/// (has "acoustic" suffix), different duration.
///
/// From EDGE-CASES.md:
/// Example: "Die Young" variants:
/// - EP "School Nights": "Die Young" (recording 91d3eb2a, duration 225901ms)
/// - Single "Bitter": "Die Young (acoustic)" (recording 60ca4db8, duration 245000ms)
///
/// These are different recordings:
/// - Different foreignRecordingId
/// - Different title (has "(acoustic)" suffix)
/// - Different duration (225s vs 245s)
///
/// Expected behavior: No match, single kept.
/// </summary>
public class AcousticTests
{
    [Fact]
    public void CleanupSinglesForArtist_AcousticVersion_DifferentTitleMbidDuration_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var acousticSingle = Album(200, "Bitter", "Single");

        var albumService = new RecordingAlbumService(importedEp, acousticSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Die Young", 225901, "91d3eb2a", fileId: 11))
            .WithTracks(acousticSingle.Id,
                Track("Die Young (acoustic)", 245000, "60ca4db8", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(acousticSingle.Id, File(901, acousticSingle.Id, "/music/die-young-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single should NOT be cleaned (acoustic doesn't match original)
        Assert.True(acousticSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_LiveVersion_DifferentTitleMbidDuration_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Studio Album", "Album");
        var liveSingle = Album(200, "Song (Live)", "Single");

        var albumService = new RecordingAlbumService(importedAlbum, liveSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "studio-mbid", fileId: 11))
            .WithTracks(liveSingle.Id,
                Track("Song (Live)", 210000, "live-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(liveSingle.Id, File(901, liveSingle.Id, "/music/song-live.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should NOT be cleaned (live version doesn't match studio)
        Assert.True(liveSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_AcousticVersion_CloseDurationButDifferentTitle_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var acousticSingle = Album(200, "Song (acoustic)", "Single");

        // Close duration but different title (acoustic vs original)
        var albumService = new RecordingAlbumService(importedAlbum, acousticSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(acousticSingle.Id,
                Track("Song (acoustic)", 182000, "acoustic-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(acousticSingle.Id, File(901, acousticSingle.Id, "/music/song-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should NOT be cleaned (different titles: "Song" vs "Song (acoustic)")
        Assert.True(acousticSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheck_AcousticVersion_NoMatch_SingleKept()
    {
        var artist = Artist();
        var album = Album(100, "School Nights", "EP");
        var acousticSingle = Album(200, "Bitter", "Single");

        var albumService = new RecordingAlbumService(album, acousticSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Die Young", 225901, "91d3eb2a", fileId: 11))
            .WithTracks(acousticSingle.Id,
                Track("Die Young (acoustic)", 245000, "60ca4db8", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(acousticSingle.Id, File(901, acousticSingle.Id, "/music/die-young-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, acousticSingle);

        // Single should NOT be cleaned (acoustic doesn't match original)
        Assert.True(acousticSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_AcousticVersion_NoMatch_SingleKept()
    {
        var artist = Artist();
        var album = Album(100, "School Nights", "EP");
        var acousticSingle = Album(200, "Bitter", "Single");

        var albumService = new RecordingAlbumService(album, acousticSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Die Young", 225901, "91d3eb2a", fileId: 11))
            .WithTracks(acousticSingle.Id,
                Track("Die Young (acoustic)", 245000, "60ca4db8", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(acousticSingle.Id, File(901, acousticSingle.Id, "/music/die-young-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single checked but skipped (no match)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.True(acousticSingle.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_MultiTrackSingleWithAcousticBSide_SingleKept()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var multiTrackSingle = Album(200, "Bitter", "Single");

        // EP has "Die Young", single has "Bitter" + "Die Young (acoustic)"
        var albumService = new RecordingAlbumService(importedEp, multiTrackSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Die Young", 225901, "91d3eb2a", fileId: 11))
            .WithTracks(multiTrackSingle.Id,
                Track("Bitter", 200000, "bitter-mbid", fileId: 21),  // Not on EP
                Track("Die Young (acoustic)", 245000, "60ca4db8", fileId: 22));  // Not on EP

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(multiTrackSingle.Id,
                File(901, multiTrackSingle.Id, "/music/bitter.flac"),
                File(902, multiTrackSingle.Id, "/music/die-young-acoustic.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single should NOT be cleaned (both tracks are unique)
        Assert.True(multiTrackSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
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