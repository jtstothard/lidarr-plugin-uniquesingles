using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC3: Remixes and Alternate Versions
///
/// Edge case: A single contains a remix or alternate version that doesn't match
/// any track on albums/EPs. The remix has a different MusicBrainz recording MBID
/// AND a different title (has "remix" suffix).
///
/// From EDGE-CASES.md:
/// Example: "Good Hurt" has 3 releases:
/// - EP "School Nights": "Good Hurt" (recording 877d1dd9)
/// - Single "Good Hurt": "Good Hurt" (recording 877d1dd9) → SAME → redundant
/// - Single "Good Hurt (Aevion remix)": "Good Hurt (Aevion remix)" (recording 580646ed)
///   → DIFFERENT → NOT redundant
///
/// Expected behavior: No match, single kept.
/// </summary>
public class RemixTests
{
    [Fact]
    public void CleanupSinglesForArtist_RemixSingle_DifferentTitleAndMbid_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var remixSingle = Album(200, "Good Hurt (Aevion remix)", "Single");

        var albumService = new RecordingAlbumService(importedEp, remixSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Good Hurt", 200000, "877d1dd9", fileId: 11))
            .WithTracks(remixSingle.Id,
                Track("Good Hurt (Aevion remix)", 210000, "580646ed", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(remixSingle.Id, File(901, remixSingle.Id, "/music/good-hurt-remix.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single should NOT be cleaned (remix doesn't match album track)
        Assert.True(remixSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_NonRemixSingle_MatchesEp_SingleCleaned()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var redundantSingle = Album(200, "Good Hurt", "Single");

        // Same MBID, different duration (within tolerance)
        var albumService = new RecordingAlbumService(importedEp, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Good Hurt", 200000, "877d1dd9", fileId: 11))
            .WithTracks(redundantSingle.Id,
                Track("Good Hurt", 201000, "877d1dd9", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/good-hurt.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single SHOULD be cleaned (Tier 1 match - same MBID)
        Assert.False(redundantSingle.Monitored);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_RemixWithSameBaseTitleButDifferentMbid_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var remixSingle = Album(200, "Song (Remix)", "Single");

        var albumService = new RecordingAlbumService(importedAlbum, remixSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(remixSingle.Id,
                Track("Song (Remix)", 210000, "remix-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(remixSingle.Id, File(901, remixSingle.Id, "/music/song-remix.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should NOT be cleaned (different title, different MBID)
        Assert.True(remixSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheck_RemixSingle_NoMatch_SingleKept()
    {
        var artist = Artist();
        var album = Album(100, "School Nights", "EP");
        var importedRemixSingle = Album(200, "Good Hurt (Aevion remix)", "Single");

        var albumService = new RecordingAlbumService(album, importedRemixSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Good Hurt", 200000, "877d1dd9", fileId: 11))
            .WithTracks(importedRemixSingle.Id,
                Track("Good Hurt (Aevion remix)", 210000, "580646ed", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedRemixSingle.Id, File(901, importedRemixSingle.Id, "/music/good-hurt-remix.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, importedRemixSingle);

        // Single should NOT be cleaned (remix doesn't match album track)
        Assert.True(importedRemixSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_RemixSingle_NoMatch_SingleKept()
    {
        var artist = Artist();
        var album = Album(100, "School Nights", "EP");
        var remixSingle = Album(200, "Good Hurt (Aevion remix)", "Single");

        var albumService = new RecordingAlbumService(album, remixSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Good Hurt", 200000, "877d1dd9", fileId: 11))
            .WithTracks(remixSingle.Id,
                Track("Good Hurt (Aevion remix)", 210000, "580646ed", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(remixSingle.Id, File(901, remixSingle.Id, "/music/good-hurt-remix.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single checked but skipped (no match)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.True(remixSingle.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_MultipleRemixVersions_AllKept()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var remixSingle1 = Album(200, "Song (Aevion remix)", "Single");
        var remixSingle2 = Album(201, "Song (Acoustic remix)", "Single");

        var albumService = new RecordingAlbumService(importedAlbum, remixSingle1, remixSingle2);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(remixSingle1.Id,
                Track("Song (Aevion remix)", 210000, "remix1-mbid", fileId: 21))
            .WithTracks(remixSingle2.Id,
                Track("Song (Acoustic remix)", 190000, "remix2-mbid", fileId: 22));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(remixSingle1.Id, File(901, remixSingle1.Id, "/music/song-aevion.flac"))
            .WithFiles(remixSingle2.Id, File(902, remixSingle2.Id, "/music/song-acoustic-remix.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Both remix singles should be kept
        Assert.True(remixSingle1.Monitored);
        Assert.True(remixSingle2.Monitored);
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