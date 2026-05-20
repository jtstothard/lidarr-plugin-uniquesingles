using System.Collections.Generic;
using FluentValidation.Results;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins;
using Xunit;
using AlbumDownloadMessage = NzbDrone.Core.Notifications.AlbumDownloadMessage;

namespace UniqueSingles.Test;

public class UniqueSinglesNotificationTests
{
    [Fact]
    public void Metadata_AndSupportedTriggerSurface_MatchCurrentConnectProviderContract()
    {
        using var loggerHarness = new LoggerHarness();
        var notification = new UniqueSinglesNotification(new RecordingCleanupService(), loggerHarness.Logger);

        Assert.Equal(UniqueSinglesPlugin.DisplayName, notification.Name);
        Assert.Equal(UniqueSinglesPlugin.RepositoryUrl, notification.Link);
        Assert.Equal(typeof(UniqueSinglesSettings), notification.ConfigContract);

        Assert.False(notification.SupportsOnGrab);
        Assert.True(notification.SupportsOnReleaseImport);
        Assert.True(notification.SupportsOnUpgrade);
        Assert.False(notification.SupportsOnRename);
        Assert.False(notification.SupportsOnArtistAdd);
        Assert.False(notification.SupportsOnArtistDelete);
        Assert.False(notification.SupportsOnAlbumDelete);
        Assert.False(notification.SupportsOnHealthIssue);
        Assert.False(notification.SupportsOnHealthRestored);
        Assert.False(notification.SupportsOnDownloadFailure);
        Assert.False(notification.SupportsOnImportFailure);
        Assert.False(notification.SupportsOnTrackRetag);
        Assert.False(notification.SupportsOnApplicationUpdate);
    }

    [Theory]
    [InlineData("Album")]
    [InlineData("album")]
    [InlineData("EP")]
    [InlineData("ep")]
    public void OnReleaseImport_AlbumOrEp_RoutesToArtistCleanup(string albumType)
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = albumType };

        notification.OnReleaseImport(Message(artist, album));

        Assert.Equal(1, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Same(artist, cleanup.LastArtist);
        Assert.Same(album, cleanup.LastAlbum);
    }

    [Theory]
    [InlineData("Single")]
    [InlineData("single")]
    public void OnReleaseImport_Single_RoutesToSingleSelfCheck(string albumType)
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = albumType };

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
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);

        notification.OnReleaseImport(Message(new Artist { Id = 10, Name = "Artist" }, new Album { Id = 20, Title = "Release", AlbumType = albumType! }));

        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("unsupported-import-type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_NullMessage_DoesNotThrowOrCallCleanup()
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(null!));

        Assert.Null(exception);
        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("null-message", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_NullArtist_DoesNotThrowOrCallCleanup()
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = null,
            Album = new Album { Id = 20, Title = "Release", AlbumType = "Album" },
        }));

        Assert.Null(exception);
        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("null-artist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_NullAlbum_DoesNotThrowOrCallCleanup()
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = new Artist { Id = 10, Name = "Artist" },
            Album = null,
        }));

        Assert.Null(exception);
        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("null-album", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_ServiceException_IsLoggedAndSwallowed()
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService { ThrowOnArtistCleanup = true };
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(Message(
            new Artist { Id = 10, Name = "Artist" },
            new Album { Id = 20, Title = "Release", AlbumType = "Album" })));

        Assert.Null(exception);
        Assert.Single(loggerHarness.Entries.Where(entry => entry.StartsWith("Warn|", StringComparison.Ordinal)));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("release-import-handler-failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public void OnReleaseImport_SingleServiceException_IsLoggedAndSwallowed()
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService { ThrowOnSingleSelfCheck = true };
        var notification = new UniqueSinglesNotification(cleanup, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(Message(
            new Artist { Id = 10, Name = "Artist" },
            new Album { Id = 20, Title = "Release", AlbumType = "Single" })));

        Assert.Null(exception);
        Assert.Single(loggerHarness.Entries.Where(entry => entry.StartsWith("Warn|", StringComparison.Ordinal)));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("release-import-handler-failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public void Test_ReturnsSuccessfulValidationResult()
    {
        using var loggerHarness = new LoggerHarness();
        var notification = new UniqueSinglesNotification(new RecordingCleanupService(), loggerHarness.Logger);

        var result = notification.Test();

        Assert.NotNull(result);
        Assert.IsType<ValidationResult>(result);
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
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
        public bool ThrowOnSingleSelfCheck { get; set; }

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

            if (ThrowOnSingleSelfCheck)
            {
                throw new InvalidOperationException("boom");
            }
        }

        public CleanupResult CleanupWithOptions(Artist artist, Album importedAlbum, SingleCleanupOptions options)
        {
            ArtistCleanupCalls++;
            LastArtist = artist;
            LastAlbum = importedAlbum;

            if (ThrowOnArtistCleanup)
            {
                throw new InvalidOperationException("boom");
            }

            return CleanupResult.Empty;
        }

        public CleanupResult ScanArtistWithOptions(Artist artist, SingleCleanupOptions options)
        {
            ArtistCleanupCalls++;
            LastArtist = artist;

            if (ThrowOnArtistCleanup)
            {
                throw new InvalidOperationException("boom");
            }

            return CleanupResult.Empty;
        }
    }

    private sealed class LoggerHarness : IDisposable
    {
        private readonly LogFactory _factory;
        private readonly MemoryTarget _target;

        public LoggerHarness()
        {
            _factory = new LogFactory();
            _target = new MemoryTarget
            {
                Layout = "${level}|${message}|${exception:format=Type,Message}",
            };

            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(_target);
            _factory.Configuration = config;
            Logger = _factory.GetLogger(Guid.NewGuid().ToString("N"));
        }

        public Logger Logger { get; }

        public IList<string> Entries
        {
            get
            {
                _factory.Flush();
                return _target.Logs;
            }
        }

        public void Dispose()
        {
            _factory.Flush();
            _factory.Dispose();
        }
    }
}
