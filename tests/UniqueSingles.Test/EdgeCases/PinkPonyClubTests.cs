using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test.EdgeCases;

/// <summary>
/// EC2: Different Recording MBIDs, Same Song (Pink Pony Club)
///
/// Edge case: Album and single have the same song with different MusicBrainz Recording MBIDs
/// but identical or very similar duration. MBID match alone would miss this case.
///
/// From EDGE-CASES.md:
/// "MusicBrainz treats these as different recordings even though they're the same song.
/// Possible reasons: Single was mastered differently, different edit/version submitted to
/// MusicBrainz, data entry inconsistency."
///
/// Example: "Pink Pony Club"
/// - Album recording: 1f79a002-85d2-424a-9b4d-a1407c8504c5
/// - Single recording: 8331bad7-f2fe-4961-a6ef-b0f87bd4e30f
/// - Duration: Both 258000ms (identical!)
///
/// Expected behavior: Tier 2 match triggers cleanup (title + duration within ±3s).
/// </summary>
public class PinkPonyClubTests
{
    [Fact]
    public void CleanupSinglesForArtist_DifferentMbidsSameSong_DurationMatch_Tier2TriggersCleanup()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "The Rise and Fall of a Midwest Princess", "Album");
        var redundantSingle = Album(200, "Pink Pony Club", "Single");

        // Same song, different MBIDs, identical duration (±3s tolerance)
        var albumService = new RecordingAlbumService(importedAlbum, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Pink Pony Club", 258000, "1f79a002-85d2-424a-9b4d-a1407c8504c5", fileId: 11))
            .WithTracks(redundantSingle.Id,
                Track("Pink Pony Club", 258000, "8331bad7-f2fe-4961-a6ef-b0f87bd4e30f", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/pink-pony-club.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single SHOULD be unmonitored and deleted (Tier 2 match)
        Assert.False(redundantSingle.Monitored);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(901, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
    }

    [Fact]
    public void CleanupSinglesForArtist_DifferentMbidsSameSong_DurationWithinTolerance_Tier2TriggersCleanup()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var redundantSingle = Album(200, "Pink Pony Club", "Single");

        // Same song, different MBIDs, duration within ±3s (diff = 1000ms)
        var albumService = new RecordingAlbumService(importedAlbum, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Pink Pony Club", 258000, "album-mbid", fileId: 11))
            .WithTracks(redundantSingle.Id,
                Track("Pink Pony Club", 259000, "single-mbid", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/pink-pony-club.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single SHOULD be unmonitored and deleted (Tier 2 match, diff=1000ms ≤ 3000ms)
        Assert.False(redundantSingle.Monitored);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_DifferentMbidsSameSong_DurationOutsideTolerance_NoCleanup()
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Album", "Album");
        var single = Album(200, "Pink Pony Club", "Single");

        // Same song, different MBIDs, duration outside ±3s (diff = 30000ms = 30s)
        // This could be a different version (radio edit vs album version)
        var albumService = new RecordingAlbumService(importedAlbum, single);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Pink Pony Club", 258000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Pink Pony Club", 228000, "single-mbid", fileId: 21));  // Radio edit?

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/pink-pony-club.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        // Single should NOT be cleaned (duration diff = 30000ms > 3000ms tolerance)
        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        // Should flag for review (Tier 3 - title-only match)
        Assert.Contains(logger.InfoMessages, m => m.Contains("review-needed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSingleSelfCheck_DifferentMbidsSameSong_Tier2TriggersCleanup()
    {
        var artist = Artist();
        var album = Album(100, "The Rise and Fall of a Midwest Princess", "Album");
        var importedSingle = Album(200, "Pink Pony Club", "Single");

        var albumService = new RecordingAlbumService(album, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Pink Pony Club", 258000, "1f79a002-85d2-424a-9b4d-a1407c8504c5", fileId: 11))
            .WithTracks(importedSingle.Id,
                Track("Pink Pony Club", 257000, "8331bad7-f2fe-4961-a6ef-b0f87bd4e30f", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedSingle.Id, File(901, importedSingle.Id, "/music/pink-pony-club.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, importedSingle);

        // Single SHOULD be unmonitored and deleted (Tier 2 match, diff=1000ms ≤ 3000ms)
        Assert.False(importedSingle.Monitored);
        Assert.Contains((importedSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void ScanArtistWithOptions_DifferentMbidsSameSong_DurationMatch_Tier2TriggersCleanup()
    {
        var artist = Artist();
        var album = Album(100, "The Rise and Fall of a Midwest Princess", "Album");
        var redundantSingle = Album(200, "Pink Pony Club", "Single");

        var albumService = new RecordingAlbumService(album, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Pink Pony Club", 258000, "1f79a002-85d2-424a-9b4d-a1407c8504c5", fileId: 11))
            .WithTracks(redundantSingle.Id,
                Track("Pink Pony Club", 258000, "8331bad7-f2fe-4961-a6ef-b0f87bd4e30f", fileId: 21));

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(901, redundantSingle.Id, "/music/pink-pony-club.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var result = service.ScanArtistWithOptions(artist, options);

        // Single checked and cleaned (Tier 2 match)
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.False(redundantSingle.Monitored);
    }

    [Fact]
    public void ScanArtistWithOptions_CustomTolerance_DifferentMbidsSameSong_UsesCustomTolerance()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Pink Pony Club", "Single");

        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Pink Pony Club", 258000, "album-mbid", fileId: 11))
            .WithTracks(single.Id,
                Track("Pink Pony Club", 228000, "single-mbid", fileId: 21));  // Diff = 30000ms

        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/pink-pony-club.flac"));

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        // Default 3000ms tolerance should NOT match
        var defaultOptions = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var defaultResult = service.ScanArtistWithOptions(artist, defaultOptions);

        Assert.Equal(1, defaultResult.CandidatesChecked);
        Assert.Equal(0, defaultResult.Cleaned);
        Assert.Equal(1, defaultResult.Skipped);
        Assert.True(single.Monitored);

        // 50000ms tolerance should match (for radio edit vs album version scenarios)
        var tolerantOptions = new SingleCleanupOptions(50000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" }, Tier3Action.FlagOnly);
        var tolerantResult = service.ScanArtistWithOptions(artist, tolerantOptions);

        Assert.Equal(1, tolerantResult.CandidatesChecked);
        Assert.Equal(1, tolerantResult.Cleaned);
        Assert.Equal(0, tolerantResult.Skipped);
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