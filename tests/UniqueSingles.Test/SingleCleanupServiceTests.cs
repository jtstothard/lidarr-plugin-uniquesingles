using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test;

public class SingleCleanupServiceTests
{
    [Theory]
    [InlineData("Album")]
    [InlineData("EP")]
    public void CleanupSinglesForArtist_AlbumOrEpImport_CleansFullyRedundantMonitoredSingle(string importedType)
    {
        var artist = Artist();
        var importedAlbum = Album(100, "Imported", importedType);
        var redundantSingle = Album(200, "Hit Single", "Single");
        var albumService = new RecordingAlbumService(importedAlbum, redundantSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(importedAlbum.Id,
                Track("Exact", 180000, "mbid-1", fileId: 11),
                Track("Duration Match", 200000, "album-mbid-2", fileId: 12))
            .WithTracks(redundantSingle.Id,
                Track("Exact", 181000, "mbid-1", fileId: 21),
                Track("Duration Match", 201000, "single-mbid-2", fileId: 22));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(redundantSingle.Id, File(900, redundantSingle.Id, "/music/single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, importedAlbum);

        Assert.Equal(new[] { artist.Id }, albumService.ArtistAlbumLookupIds);
        Assert.Empty(albumService.AllLibraryLookupCalls);
        Assert.Contains((redundantSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.False(redundantSingle.Monitored);
        Assert.Equal(new[] { redundantSingle.Id }, mediaFileService.AlbumLookupIds);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(900, deleteMediaFiles.DeletedFiles[0].TrackFile.Id);
        Assert.Empty(trackService.ArtistLookupIds);
    }

    [Fact]
    public void CleanupSingleSelfCheck_RedundantImportedSingle_CleansItself()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var importedSingle = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, importedSingle);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(importedSingle.Id, Track("Song", 181000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(importedSingle.Id, File(901, importedSingle.Id, "/music/imported-single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSingleSelfCheck(artist, importedSingle);

        Assert.Equal(new[] { artist.Id }, albumService.ArtistAlbumLookupIds);
        Assert.Contains((importedSingle.Id, false), albumService.SetMonitoredCalls);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(importedSingle.Id, deleteMediaFiles.DeletedFiles[0].TrackFile.AlbumId);
    }

    [Fact]
    public void CleanupSinglesForArtist_UnmatchedSingle_DoesNotUnmonitorOrDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Exclusive", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Album Song", 180000, "album-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Exclusive B Side", 190000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/exclusive.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_Tier3OnlySingle_LogsReviewAndDoesNotUnmonitorOrDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Maybe Different Version", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 220000, "album-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "single-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/tier3.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("review-needed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSinglesForArtist_UnmonitoredSingle_IsSkippedWithoutTrackLookupOrDelete()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Already Done", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        Assert.DoesNotContain(single.Id, trackService.AlbumLookupIds);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_SingleWithNoDownloadedTracks_IsSkipped()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Not Downloaded", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 0));
        var mediaFileService = new RecordingMediaFileService();
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSinglesForArtist_UnmonitorFailure_SkipsDeleteAndDoesNotThrow()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single)
        {
            ThrowOnSetAlbumMonitored = true,
        };
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/would-delete.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var exception = Record.Exception(() => service.CleanupSinglesForArtist(artist, album));

        Assert.Null(exception);
        Assert.True(single.Monitored);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.WarnMessages, m => m.Contains("unmonitor failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanupSinglesForArtist_DeleteFailure_IsLoggedAndSwallowedAfterUnmonitor()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var logger = new RecordingLogger();
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService()
            .WithFiles(single.Id, File(901, single.Id, "/music/delete-fails.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles { ThrowOnDelete = true };
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger);

        var exception = Record.Exception(() => service.CleanupSinglesForArtist(artist, album));

        Assert.Null(exception);
        Assert.False(single.Monitored);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.WarnMessages, m => m.Contains("delete failure", StringComparison.OrdinalIgnoreCase));
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

        public bool ThrowOnSetAlbumMonitored { get; set; }
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
