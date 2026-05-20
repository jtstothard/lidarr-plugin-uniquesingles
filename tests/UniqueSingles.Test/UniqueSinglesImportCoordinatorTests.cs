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
    public static IEnumerable<object[]> SupportedImportCases()
    {
        yield return new object[]
        {
            "Album",
            "artist-cleanup",
            1,
            0,
            new CleanupResult(4, 2, 1, 0, 0, 1),
            4500,
            new[] { "Album", "Single" },
            Tier3Action.Skip,
        };

        yield return new object[]
        {
            "EP",
            "artist-cleanup",
            1,
            0,
            new CleanupResult(3, 0, 2, 1, 0, 0),
            4100,
            new[] { "Album", "EP" },
            Tier3Action.FlagOnly,
        };

        yield return new object[]
        {
            "Single",
            "single-self-check",
            0,
            1,
            new CleanupResult(1, 1, 0, 0, 0, 0),
            3200,
            new[] { "Album", "EP" },
            Tier3Action.Skip,
        };

        yield return new object[]
        {
            "single",
            "single-self-check",
            0,
            1,
            new CleanupResult(1, 0, 1, 1, 0, 0),
            3200,
            new[] { "Album", "EP" },
            Tier3Action.FlagOnly,
        };
    }

    [Theory]
    [MemberData(nameof(SupportedImportCases))]
    public void HandleImport_SupportedRoutes_LogDeterministicAggregateCleanupOutcomes(
        string albumType,
        string expectedRoute,
        int expectedArtistCleanupCalls,
        int expectedSingleSelfCheckCalls,
        CleanupResult expectedResult,
        int durationToleranceMs,
        string[] comparisonReleaseTypes,
        Tier3Action tier3Action)
    {
        using var loggerHarness = new LoggerHarness();
        var cleanup = new RecordingCleanupService
        {
            ArtistCleanupResult = expectedRoute == "artist-cleanup" ? expectedResult : CleanupResult.Empty,
            SingleSelfCheckResult = expectedRoute == "single-self-check" ? expectedResult : CleanupResult.Empty,
        };
        var coordinator = new UniqueSinglesImportCoordinator(cleanup, loggerHarness.Logger);
        var options = new SingleCleanupOptions(durationToleranceMs, new HashSet<string>(comparisonReleaseTypes), tier3Action);
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = albumType };

        var result = coordinator.HandleImport(Message(artist, album), options);

        Assert.Equal(expectedArtistCleanupCalls, cleanup.ArtistCleanupCalls);
        Assert.Equal(expectedSingleSelfCheckCalls, cleanup.SingleSelfCheckCalls);
        Assert.Same(artist, cleanup.LastArtist);
        Assert.Same(album, cleanup.LastAlbum);
        Assert.Same(options, cleanup.LastOptions);

        Assert.Equal(expectedResult.CandidatesChecked, result.CandidatesChecked);
        Assert.Equal(expectedResult.Cleaned, result.Cleaned);
        Assert.Equal(expectedResult.Skipped, result.Skipped);
        Assert.Equal(expectedResult.ReviewNeeded, result.ReviewNeeded);
        Assert.Equal(expectedResult.UnmonitorFailures, result.UnmonitorFailures);
        Assert.Equal(expectedResult.DeleteFailures, result.DeleteFailures);

        var routeEntry = Assert.Single(loggerHarness.Entries.Where(entry =>
            entry.Contains("UniqueSingles import route:", StringComparison.Ordinal) &&
            entry.Contains($"route={expectedRoute}", StringComparison.Ordinal)));
        var resultEntry = Assert.Single(loggerHarness.Entries.Where(entry =>
            entry.Contains("UniqueSingles import result:", StringComparison.Ordinal) &&
            entry.Contains($"route={expectedRoute}", StringComparison.Ordinal)));

        Assert.Contains($"albumType='{albumType}'", routeEntry, StringComparison.Ordinal);
        Assert.Contains($"comparisonReleaseTypes='{FormatComparisonReleaseTypes(comparisonReleaseTypes)}'", routeEntry, StringComparison.Ordinal);
        Assert.Contains($"durationToleranceMs={durationToleranceMs}", routeEntry, StringComparison.Ordinal);
        Assert.Contains($"tier3Action='{tier3Action}'", routeEntry, StringComparison.Ordinal);

        Assert.Contains($"albumType='{albumType}'", resultEntry, StringComparison.Ordinal);
        Assert.Contains($"candidatesChecked={expectedResult.CandidatesChecked}", resultEntry, StringComparison.Ordinal);
        Assert.Contains($"cleaned={expectedResult.Cleaned}", resultEntry, StringComparison.Ordinal);
        Assert.Contains($"skipped={expectedResult.Skipped}", resultEntry, StringComparison.Ordinal);
        Assert.Contains($"reviewNeeded={expectedResult.ReviewNeeded}", resultEntry, StringComparison.Ordinal);
        Assert.Contains($"unmonitorFailures={expectedResult.UnmonitorFailures}", resultEntry, StringComparison.Ordinal);
        Assert.Contains($"deleteFailures={expectedResult.DeleteFailures}", resultEntry, StringComparison.Ordinal);
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

    private static string FormatComparisonReleaseTypes(IEnumerable<string> comparisonReleaseTypes)
    {
        return string.Join(",", comparisonReleaseTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase));
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
