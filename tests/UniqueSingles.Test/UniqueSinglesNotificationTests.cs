using NLog;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test;

public class UniqueSinglesNotificationTests
{
    [Theory]
    [InlineData("Album")]
    [InlineData("album")]
    [InlineData("EP")]
    [InlineData("ep")]
    public void OnReleaseImport_AlbumOrEp_RoutesToArtistCleanup(string albumType)
    {
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, new RecordingLogger());
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = albumType };

        notification.OnReleaseImport(Message(artist, album));

        Assert.Equal(1, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Same(artist, cleanup.LastArtist);
        Assert.Same(album, cleanup.LastAlbum);
    }

    [Fact]
    public void OnReleaseImport_Single_RoutesToSingleSelfCheck()
    {
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, new RecordingLogger());
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = "single" };

        notification.OnReleaseImport(Message(artist, album));

        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(1, cleanup.SingleSelfCheckCalls);
        Assert.Same(artist, cleanup.LastArtist);
        Assert.Same(album, cleanup.LastAlbum);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Compilation")]
    public void OnReleaseImport_UnsupportedReleaseType_DoesNotCallCleanup(string? albumType)
    {
        var cleanup = new RecordingCleanupService();
        var logger = new RecordingLogger();
        var notification = new UniqueSinglesNotification(cleanup, logger);

        notification.OnReleaseImport(Message(new Artist { Id = 10, Name = "Artist" }, new Album { Id = 20, Title = "Release", AlbumType = albumType! }));

        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Contains(logger.InfoMessages, m => m.Contains("no-op", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_NullMessage_DoesNotThrowOrCallCleanup()
    {
        var cleanup = new RecordingCleanupService();
        var logger = new RecordingLogger();
        var notification = new UniqueSinglesNotification(cleanup, logger);

        notification.OnReleaseImport(null!);

        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Contains(logger.InfoMessages, m => m.Contains("null-message", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_ServiceException_IsLoggedAndSwallowed()
    {
        var cleanup = new RecordingCleanupService { ThrowOnArtistCleanup = true };
        var logger = new RecordingLogger();
        var notification = new UniqueSinglesNotification(cleanup, logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(Message(
            new Artist { Id = 10, Name = "Artist" },
            new Album { Id = 20, Title = "Release", AlbumType = "Album" })));

        Assert.Null(exception);
        Assert.Single(logger.WarnExceptions);
    }

    [Fact]
    public void Test_ReturnsPassingValidationResult()
    {
        var notification = new UniqueSinglesNotification(new RecordingCleanupService(), new RecordingLogger());

        Assert.Null(notification.Test());
    }

    private static AlbumDownloadMessage Message(Artist artist, Album album)
    {
        return new AlbumDownloadMessage
        {
            Artist = artist,
            Album = album,
        };
    }

    private sealed class RecordingCleanupService : ISingleCleanupService
    {
        public int ArtistCleanupCalls { get; private set; }
        public int SingleSelfCheckCalls { get; private set; }
        public Artist? LastArtist { get; private set; }
        public Album? LastAlbum { get; private set; }
        public bool ThrowOnArtistCleanup { get; set; }

        public void CleanupSinglesForArtist(Artist artist, Album importedAlbum)
        {
            ArtistCleanupCalls++;
            LastArtist = artist;
            LastAlbum = importedAlbum;

            if (ThrowOnArtistCleanup)
            {
                throw new InvalidOperationException("boom");
            }
        }

        public void CleanupSingleSelfCheck(Artist artist, Album importedSingle)
        {
            SingleSelfCheckCalls++;
            LastArtist = artist;
            LastAlbum = importedSingle;
        }
    }

    private sealed class RecordingLogger : Logger
    {
        public List<string> InfoMessages { get; } = new();
        public List<Exception> WarnExceptions { get; } = new();

        public override void Info(string message, params object?[] args)
        {
            InfoMessages.Add(message);
        }

        public override void Warn(Exception exception, string message, params object?[] args)
        {
            WarnExceptions.Add(exception);
        }
    }
}
