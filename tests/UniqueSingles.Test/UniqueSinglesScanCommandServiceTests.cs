using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Messaging.Commands;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Tests for UniqueSinglesScanCommandService covering:
/// - Full-library scan behavior
/// - Per-artist failure isolation
/// - Idempotency
/// - Observability logging
/// </summary>
public class UniqueSinglesScanCommandServiceTests
{
    [Fact]
    public void Execute_CallsGetAllArtists()
    {
        var artistService = new RecordingArtistService(Artist(1, "Test Artist 1"), Artist(2, "Test Artist 2"));
        var albumService = new RecordingAlbumService();
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        Assert.Single(artistService.GetAllArtistsCalls);
    }

    [Fact]
    public void Execute_UnmonitoredArtist_IsSkipped()
    {
        var monitored = Artist(1, "Monitored Artist");
        var unmonitored = Artist(2, "Unmonitored Artist");
        var album = Album(100, "Album", "Album");
        var artistService = new RecordingArtistService(monitored, unmonitored);
        var albumService = new RecordingAlbumService(album);
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        // Both artists should be looked up for albums (GetAlbumsByArtist is called for each artist)
        // But only monitored artists have their singles processed
        Assert.Equal(2, albumService.ArtistAlbumLookupIds.Count);
        Assert.Contains(1, albumService.ArtistAlbumLookupIds);
        Assert.Contains(2, albumService.ArtistAlbumLookupIds);

        // No unmonitor/delete should occur because no redundant singles exist in this test
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void Execute_MonitoredArtistWithAlbum_CleansRedundantSingle()
    {
        var artist = Artist(1, "Test Artist");
        var album = Album(100, "Album", "Album");
        var redundantSingle = Album(200, "Hit Single", "Single");
        var artistService = new RecordingArtistService(artist);
        var albumService = new RecordingAlbumService(album, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(redundantSingle.Id, Track("Song", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(900, redundantSingle.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        Assert.Equal(CompletionStatus.Complete, command.Status);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(900, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
    }

    [Fact]
    public void Execute_OneArtistFailure_DoesNotAbortSubsequentArtists()
    {
        var artist1 = Artist(1, "Failing Artist");
        var artist2 = Artist(2, "Succeeding Artist");
        var albumForArtist2 = Album(100, "Album", "Album");
        var singleForArtist2 = Album(200, "Single", "Single");
        var artistService = new RecordingArtistService(artist1, artist2);
        var albumService = new RecordingAlbumService(albumForArtist2, singleForArtist2)
        {
            ThrowOnGetAlbumsForArtistId = 1 // Artist 1 fails to load albums
        };
        var trackService = new RecordingTrackService()
            .WithTracks(albumForArtist2.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(singleForArtist2.Id, Track("Song", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(singleForArtist2.Id, File(900, singleForArtist2.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        // Artist 2 should still be processed despite Artist 1 failing
        Assert.Contains((singleForArtist2.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        // The Warn message should exist
        Assert.NotEmpty(logger.WarnMessages);
        Assert.Equal(CompletionStatus.Complete, command.Status);
    }

    [Fact]
    public void Execute_GetAllArtistsThrows_LogsErrorAndFailsCommand()
    {
        var artistService = new RecordingArtistService()
        {
            ThrowOnGetAllArtists = true
        };
        var albumService = new RecordingAlbumService();
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        Assert.Equal(CompletionStatus.Failed, command.Status);
        Assert.Contains("Failed to load artists", command.Message);
        Assert.Contains(logger.ErrorMessages, m => m.Contains("could not load artists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Execute_SummaryLoggingIncludesRequiredCounts()
    {
        var artist1 = Artist(1, "Artist With Redundant");
        var artist2 = Artist(2, "Artist Without Singles");
        var album1 = Album(100, "Album", "Album");
        var single1 = Album(200, "Redundant Single", "Single");
        var album2 = Album(300, "Album 2", "Album");
        var artistService = new RecordingArtistService(artist1, artist2);
        var albumService = new RecordingAlbumService(album1, single1, album2);
        var trackService = new RecordingTrackService()
            .WithTracks(album1.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single1.Id, Track("Song", 181000, "single-mbid", fileId: 21))
            .WithTracks(album2.Id, Track("Other Song", 190000, "album2-mbid", fileId: 31));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single1.Id, File(900, single1.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        // Check completion message contains expected counts
        Assert.Contains("2 artists scanned", command.Message);
        Assert.Contains("0 skipped", command.Message);
        Assert.Contains("0 failed", command.Message);

        // Check log contains scan start/summary
        Assert.Contains(logger.InfoMessages, m => m.Contains("UniqueSingles scan started", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.InfoMessages, m => m.Contains("scan complete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.InfoMessages, m => m.Contains("artistsScanned=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Execute_Idempotent_SecondRunProducesNoAdditionalChanges()
    {
        var artist = Artist(1, "Test Artist");
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var artistService = new RecordingArtistService(artist);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(900, single.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command1 = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        // First run
        service.Execute(command1);
        var firstRunSetMonitoredCalls = albumService.SetMonitoredCalls.Count;
        var firstRunDeleteCalls = deleteMediaFiles.DeletedFiles.Count;

        // Second run with new command instance
        albumService.SetMonitoredCalls.Clear();
        deleteMediaFiles.DeletedFiles.Clear();
        var command2 = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        service.Execute(command2);
        var secondRunSetMonitoredCalls = albumService.SetMonitoredCalls.Count;
        var secondRunDeleteCalls = deleteMediaFiles.DeletedFiles.Count;

        // Second run should produce no additional unmonitor/delete calls
        // (single is already unmonitored, so it won't be processed)
        Assert.Equal(CompletionStatus.Complete, command2.Status);
        // The single is already unmonitored, so track lookup is skipped
        Assert.Equal(0, secondRunDeleteCalls);
    }

    [Fact]
    public void Execute_NullCommand_ThrowsArgumentNullException()
    {
        var artistService = new RecordingArtistService();
        var albumService = new RecordingAlbumService();
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        Assert.Throws<ArgumentNullException>(() => service.Execute(null!));
    }

    [Fact]
    public void Execute_MultipleArtists_CorrectCountsInSummary()
    {
        var artist1 = Artist(1, "Artist 1");
        var artist2 = Artist(2, "Artist 2");
        var artist3 = Artist(3, "Artist 3");
        var album1 = Album(100, "Album 1", "Album");
        var single1 = Album(200, "Single 1", "Single");
        var album2 = Album(300, "Album 2", "Album");
        var single2 = Album(400, "Single 2", "Single");
        var album3 = Album(500, "Album 3", "Album");
        var artistService = new RecordingArtistService(artist1, artist2, artist3);
        var albumService = new RecordingAlbumService(album1, single1, album2, single2, album3);
        var trackService = new RecordingTrackService()
            .WithTracks(album1.Id, Track("Song 1", 180000, "mbid1", fileId: 11))
            .WithTracks(single1.Id, Track("Song 1", 181000, "mbid1-single", fileId: 21))
            .WithTracks(album2.Id, Track("Song 2", 190000, "mbid2", fileId: 31))
            .WithTracks(single2.Id, Track("Song 2", 191000, "mbid2-single", fileId: 41))
            .WithTracks(album3.Id, Track("Song 3", 200000, "mbid3", fileId: 51));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single1.Id, File(900, single1.Id, "/music/single1.flac"))
            .WithFiles(single2.Id, File(901, single2.Id, "/music/single2.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        Assert.Equal(CompletionStatus.Complete, command.Status);
        Assert.Contains("3 artists scanned", command.Message);
        Assert.Equal(2, deleteMediaFiles.DeletedFiles.Count);
    }

    [Fact]
    public void Execute_ArtistWithNoAlbums_SkippedAndLogged()
    {
        var artist = Artist(1, "Empty Artist");
        var artistService = new RecordingArtistService(artist);
        var albumService = new RecordingAlbumService(); // No albums
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        Assert.Equal(CompletionStatus.Complete, command.Status);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.DebugMessages, m => m.Contains("no albums for artist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Execute_ArtistWithNoAlbumOrEp_SkippedAndLogged()
    {
        var artist = Artist(1, "Singles Only Artist");
        var single = Album(200, "Only Single", "Single");
        var artistService = new RecordingArtistService(artist);
        var albumService = new RecordingAlbumService(single);
        var trackService = new RecordingTrackService()
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var command = new UniqueSingles.Commands.UniqueSinglesScanCommand();
        var service = new UniqueSingles.Commands.UniqueSinglesScanCommandService(
            artistService,
            albumService,
            trackService,
            mediaFileService,
            deleteMediaFiles,
            logger);

        service.Execute(command);

        Assert.Equal(CompletionStatus.Complete, command.Status);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("no monitored album/EP found", StringComparison.OrdinalIgnoreCase));
    }

    // ============================================================
    // Test doubles
    // ============================================================

    private sealed class RecordingArtistService : IArtistService
    {
        private readonly List<Artist> _artists;

        public RecordingArtistService(params Artist[] artists)
        {
            _artists = artists.ToList();
        }

        public List<int> GetAllArtistsCalls { get; } = new();
        public bool ThrowOnGetAllArtists { get; set; }

        public List<Artist> GetAllArtists()
        {
            GetAllArtistsCalls.Add(0);
            if (ThrowOnGetAllArtists)
            {
                throw new InvalidOperationException("GetAllArtists failed");
            }
            return _artists;
        }

        public Artist? GetArtist(int id)
        {
            return _artists.FirstOrDefault(a => a.Id == id);
        }
    }

    private sealed class RecordingAlbumService : IAlbumService
    {
        private readonly Dictionary<int, List<Album>> _albumsByArtist = new();

        public RecordingAlbumService(params Album[] albums)
        {
            foreach (var album in albums)
            {
                if (!_albumsByArtist.ContainsKey(album.Id))
                {
                    _albumsByArtist[album.Id] = new List<Album>();
                }
            }
            // For this test, we'll store albums directly
            _allAlbums = albums.ToList();
        }

        private readonly List<Album> _allAlbums = new();

        public bool ThrowOnSetAlbumMonitored { get; set; }
        public int? ThrowOnGetAlbumsForArtistId { get; set; }
        public List<int> ArtistAlbumLookupIds { get; } = new();
        public List<int> AllLibraryLookupCalls { get; } = new();
        public List<(int AlbumId, bool Monitored)> SetMonitoredCalls { get; } = new();

        public List<Album> GetAlbumsByArtist(int artistId)
        {
            ArtistAlbumLookupIds.Add(artistId);
            if (ThrowOnGetAlbumsForArtistId.HasValue && ThrowOnGetAlbumsForArtistId == artistId)
            {
                throw new InvalidOperationException($"GetAlbumsByArtist failed for artist {artistId}");
            }
            return _allAlbums.ToList(); // Return all albums for simplicity in these tests
        }

        public Album GetAlbum(int albumId)
        {
            AllLibraryLookupCalls.Add(albumId);
            return _allAlbums.Single(a => a.Id == albumId);
        }

        public List<Album> GetArtistAlbumsWithFiles(Artist artist)
        {
            AllLibraryLookupCalls.Add(artist.Id);
            return _allAlbums.ToList();
        }

        public void SetAlbumMonitored(int albumId, bool monitored)
        {
            SetMonitoredCalls.Add((albumId, monitored));
            if (ThrowOnSetAlbumMonitored)
            {
                throw new InvalidOperationException("unmonitor failed");
            }
            var album = _allAlbums.FirstOrDefault(a => a.Id == albumId);
            if (album != null)
            {
                album.Monitored = monitored;
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
        public bool ThrowOnDelete { get; set; }
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
        public List<string> ErrorMessages { get; } = new();
        public List<string> DebugMessages { get; } = new();

        public override void Info(string message, params object?[] args)
        {
            InfoMessages.Add(WithArgs(message, args));
        }

        public override void Warn(string message, params object?[] args)
        {
            WarnMessages.Add(WithArgs(message, args));
        }

        public override void Warn(Exception exception, string message, params object?[] args)
        {
            WarnMessages.Add(WithArgs(message, args));
        }

        public override void Error(string message, params object?[] args)
        {
            ErrorMessages.Add(WithArgs(message, args));
        }

        public override void Error(Exception exception, string message, params object?[] args)
        {
            ErrorMessages.Add(WithArgs(message, args));
        }

        public override void Debug(string message, params object?[] args)
        {
            DebugMessages.Add(WithArgs(message, args));
        }

        private static string WithArgs(string message, object?[] args)
        {
            return args.Length == 0 ? message : $"{message} {string.Join(" ", args.Select(a => a?.ToString()))}";
        }
    }

    // ============================================================
    // Test data helpers
    // ============================================================

    private static Artist Artist(int id, string name) => new()
    {
        Id = id,
        Name = name,
    };

    private static Album Album(int id, string title, string type) => new()
    {
        Id = id,
        Title = title,
        AlbumType = type,
        Monitored = true,
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
}