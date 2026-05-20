using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins;
using Xunit;
using AlbumDownloadMessage = NzbDrone.Core.Notifications.AlbumDownloadMessage;

namespace UniqueSingles.Test;

public class UniqueSinglesImportCoordinatorTests
{
    [Theory]
    [InlineData("Album")]
    [InlineData("album")]
    [InlineData("EP")]
    [InlineData("ep")]
    public void HandleImport_AlbumOrEp_RoutesToArtistCleanupWithSummaryLogging(string albumType)
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService
        {
            ArtistCleanupResult = new CleanupResult(4, 2, 1, 0, 0, 1),
        };
        var coordinator = new UniqueSinglesImportCoordinator(cleanup, loggerHarness.Logger);
        var options = new SingleCleanupOptions(4500, new HashSet<string> { "Album", "Single" }, Tier3Action.Skip);
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = albumType };

        var result = coordinator.HandleImport(Message(artist, album), options);

        Assert.Equal(1, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Same(artist, cleanup.LastArtist);
        Assert.Same(album, cleanup.LastAlbum);
        Assert.Same(options, cleanup.LastOptions);
        Assert.Equal(4, result.CandidatesChecked);
        Assert.Equal(2, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.DeleteFailures);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("UniqueSingles import route:", StringComparison.Ordinal) && entry.Contains("route=artist-cleanup", StringComparison.Ordinal));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("comparisonReleaseTypes='Album,Single'", StringComparison.Ordinal));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("durationToleranceMs=4500", StringComparison.Ordinal));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("tier3Action='Skip'", StringComparison.Ordinal));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("UniqueSingles import result:", StringComparison.Ordinal) && entry.Contains("candidatesChecked=4", StringComparison.Ordinal) && entry.Contains("cleaned=2", StringComparison.Ordinal) && entry.Contains("deleteFailures=1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Single")]
    [InlineData("single")]
    public void HandleImport_Single_RoutesToSingleSelfCheckWithSummaryLogging(string albumType)
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService
        {
            SingleSelfCheckResult = new CleanupResult(1, 0, 1, 1, 0, 0),
        };
        var coordinator = new UniqueSinglesImportCoordinator(cleanup, loggerHarness.Logger);
        var options = new SingleCleanupOptions(3200, new HashSet<string> { "Album", "EP" }, Tier3Action.FlagOnly);
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = albumType };

        var result = coordinator.HandleImport(Message(artist, album), options);

        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(1, cleanup.SingleSelfCheckCalls);
        Assert.Same(artist, cleanup.LastArtist);
        Assert.Same(album, cleanup.LastAlbum);
        Assert.Same(options, cleanup.LastOptions);
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.ReviewNeeded);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("route=single-self-check", StringComparison.Ordinal));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("reviewNeeded=1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Compilation")]
    public void HandleImport_UnsupportedReleaseType_LogsExplicitNoOpAndDoesNotCallCleanup(string? albumType)
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService();
        var coordinator = new UniqueSinglesImportCoordinator(cleanup, loggerHarness.Logger);

        var result = coordinator.HandleImport(
            Message(
                new Artist { Id = 10, Name = "Artist" },
                new Album { Id = 20, Title = "Release", AlbumType = albumType! }),
            new SingleCleanupOptions(3000, new HashSet<string> { "Album", "EP" }, Tier3Action.FlagOnly));

        Assert.Equal(0, cleanup.ArtistCleanupCalls);
        Assert.Equal(0, cleanup.SingleSelfCheckCalls);
        Assert.Equal(0, result.CandidatesChecked);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("unsupported-import-type", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("route=no-op", StringComparison.Ordinal));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("candidatesChecked=0", StringComparison.Ordinal) && entry.Contains("cleaned=0", StringComparison.Ordinal) && entry.Contains("skipped=0", StringComparison.Ordinal));
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
        public SingleCleanupOptions? LastOptions { get; private set; }
        public CleanupResult ArtistCleanupResult { get; set; } = CleanupResult.Empty;
        public CleanupResult SingleSelfCheckResult { get; set; } = CleanupResult.Empty;

        public void CleanupSinglesForArtist(Artist artist, Album importedAlbum)
        {
            throw new NotSupportedException("Legacy routing should not be used by the import coordinator.");
        }

        public void CleanupSingleSelfCheck(Artist artist, Album importedSingle)
        {
            throw new NotSupportedException("Legacy routing should not be used by the import coordinator.");
        }

        public CleanupResult CleanupSingleSelfCheckWithOptions(Artist artist, Album importedSingle, SingleCleanupOptions options)
        {
            SingleSelfCheckCalls++;
            LastArtist = artist;
            LastAlbum = importedSingle;
            LastOptions = options;
            return SingleSelfCheckResult;
        }

        public CleanupResult CleanupWithOptions(Artist artist, Album importedAlbum, SingleCleanupOptions options)
        {
            ArtistCleanupCalls++;
            LastArtist = artist;
            LastAlbum = importedAlbum;
            LastOptions = options;
            return ArtistCleanupResult;
        }

        public CleanupResult ScanArtistWithOptions(Artist artist, SingleCleanupOptions options)
        {
            throw new NotSupportedException();
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
