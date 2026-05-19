using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC5: EP/Single Name Collision
///
/// Edge case: An EP and a single share the same name but have different tracks.
/// The single track does NOT appear on the EP, so the single is NOT redundant.
///
/// From EDGE-CASES.md:
/// Example: "School Nights" exists as BOTH:
/// - EP "School Nights" (ID 8184) — 5 tracks, albumType "EP"
/// - Single "School Nights" (ID 8186) — 1 track, albumType "Single"
///
/// The single track "School Nights" (recording 05bf6409) does NOT appear on the EP.
/// EP has: "Die Young", "Good Hurt", "Meantime", "Sugar High", "Bad for You"
///
/// Expected behavior: No match, single kept.
/// </summary>
public class EpSingleNameCollisionTests
{
    [Fact]
    public void CleanupSinglesForArtist_EpAndSingleSameNameButDifferentTracks_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var sameNameSingle = Album(200, "School Nights", "Single");

        // EP has different tracks than single
        var albumService = new RecordingAlbumService(importedEp, sameNameSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Die Young", 225901, "die-young-mbid", fileId: 11),
                Track("Good Hurt", 200000, "good-hurt-mbid", fileId: 12),
                Track("Meantime", 190000, "meantime-mbid", fileId: 13),
                Track("Sugar High", 195000, "sugar-high-mbid", fileId: 14),
                Track("Bad for You", 210000, "bad-for-you-mbid", fileId: 15))
            .WithTracks(sameNameSingle.Id,
                Track("School Nights", 200000, "school-nights-single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(sameNameSingle.Id, File(901, sameNameSingle.Id, "/music/school-nights.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single should NOT be cleaned (track not on EP)
        Assert.True(sameNameSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_SingleTrackAppearsOnEp_SingleCleaned()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var redundantSingle = Album(200, "Die Young", "Single");

        // Single track IS on EP
        var albumService = new RecordingAlbumService(importedEp, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Die Young", 225901, "die-young-mbid", fileId: 11),
                Track("Good Hurt", 200000, "good-hurt-mbid", fileId: 12))
            .WithTracks(redundantSingle.Id,
                Track("Die Young", 225901, "die-young-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/die-young.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single SHOULD be cleaned (Tier 1 match)
        Assert.False(redundantSingle.Monitored);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheck_EpAndSingleSameNameButDifferentTracks_NoMatch_SingleKept()
    {
        var artist = Artist();
        var ep = Album(100, "School Nights", "EP");
        var sameNameSingle = Album(200, "School Nights", "Single");

        var albumService = new RecordingAlbumService(ep, sameNameSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(ep.Id,
                Track("Die Young", 225901, "die-young-mbid", fileId: 11),
                Track("Good Hurt", 200000, "good-hurt-mbid", fileId: 12),
                Track("Meantime", 190000, "meantime-mbid", fileId: 13))
            .WithTracks(sameNameSingle.Id,
                Track("School Nights", 200000, "school-nights-single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(sameNameSingle.Id, File(901, sameNameSingle.Id, "/music/school-nights.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, sameNameSingle);

        // Single should NOT be cleaned (track not on EP)
        Assert.True(sameNameSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_EpAndSingleSameNameButDifferentTracks_NoMatch_SingleKept()
    {
        var artist = Artist();
        var ep = Album(100, "School Nights", "EP");
        var sameNameSingle = Album(200, "School Nights", "Single");

        var albumService = new RecordingAlbumService(ep, sameNameSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(ep.Id,
                Track("Die Young", 225901, "die-young-mbid", fileId: 11),
                Track("Good Hurt", 200000, "good-hurt-mbid", fileId: 12),
                Track("Meantime", 190000, "meantime-mbid", fileId: 13),
                Track("Sugar High", 195000, "sugar-high-mbid", fileId: 14),
                Track("Bad for You", 210000, "bad-for-you-mbid", fileId: 15))
            .WithTracks(sameNameSingle.Id,
                Track("School Nights", 200000, "school-nights-single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(sameNameSingle.Id, File(901, sameNameSingle.Id, "/music/school-nights.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single checked but skipped (no match)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.True(sameNameSingle.Monitored);
    }

    [Fact]
    public void CleanupSinglesForArtist_EpAndSingleWithSameTrackName_ButDifferentMbids_NoMatch_SingleKept()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var sameNameSingle = Album(200, "School Nights", "Single");

        // Same track title but different MBIDs (different recordings)
        var albumService = new RecordingAlbumService(importedEp, sameNameSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Die Young", 225901, "die-young-ep-mbid", fileId: 11),
                Track("School Nights", 200000, "school-nights-ep-mbid", fileId: 12))
            .WithTracks(sameNameSingle.Id,
                Track("School Nights", 210000, "school-nights-single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(sameNameSingle.Id, File(901, sameNameSingle.Id, "/music/school-nights.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single should NOT be cleaned (different MBIDs, duration outside tolerance)
        Assert.True(sameNameSingle.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_EpAndSingleWithSameTrackName_DurationWithinTolerance_SingleCleaned()
    {
        var artist = Artist();
        var importedEp = Album(100, "School Nights", "EP");
        var sameNameSingle = Album(200, "School Nights", "Single");

        // Same track title, different MBIDs, but duration within tolerance
        var albumService = new RecordingAlbumService(importedEp, sameNameSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedEp.Id,
                Track("Die Young", 225901, "die-young-ep-mbid", fileId: 11),
                Track("School Nights", 200000, "school-nights-ep-mbid", fileId: 12))
            .WithTracks(sameNameSingle.Id,
                Track("School Nights", 201000, "school-nights-single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(sameNameSingle.Id, File(901, sameNameSingle.Id, "/music/school-nights.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedEp);

        // Single SHOULD be cleaned (Tier 2 match: title + duration)
        Assert.False(sameNameSingle.Monitored);
        Assert.Contains((sameNameSingle.Id, false), albumService.SetMonitoredCalls);
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