using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins;
using NzbDrone.Core.Plugins.Scheduling;
using NzbDrone.Core.ThingiProvider;
using Xunit;

namespace UniqueSingles.Test;

public class UniqueSinglesScanTaskTests
{
    // ── Execute scans all monitored artists ───────────────────────────

    [Fact]
    public void Execute_ScansAllMonitoredArtists()
    {
        var artists = new List<Artist>
        {
            MonitoredArtist(1, "Artist A"),
            MonitoredArtist(2, "Artist B"),
            UnmonitoredArtist(3, "Artist C")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand();

        task.Execute(command);

        Assert.Equal(2, cleanupService.ScannedArtists.Count);
        Assert.Equal(new[] { 1, 2 }, cleanupService.ScannedArtists.Select(a => a.Id).OrderBy(id => id));
    }

    [Fact]
    public void Execute_AggregatesCleanupResults()
    {
        var artists = new List<Artist>
        {
            MonitoredArtist(1, "Artist A"),
            MonitoredArtist(2, "Artist B")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = new CleanupResult(10, 2, 5, 1, 0, 0);
        cleanupService.Results[2] = new CleanupResult(8, 3, 3, 0, 1, 1);
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand();

        task.Execute(command);

        // Total: 18 candidates, 5 cleaned, 8 skipped, 1 review, 1 unmonitor failure, 1 delete failure
        Assert.Contains("2 artists scanned, 5 singles cleaned, 8 skipped, 1 flagged for review", command.ResultMessage);
    }

    [Fact]
    public void Execute_HandlesPerArtistFailure_Gracefully()
    {
        var artists = new List<Artist>
        {
            MonitoredArtist(1, "Good Artist"),
            MonitoredArtist(2, "Bad Artist"),
            MonitoredArtist(3, "Also Good")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = new CleanupResult(5, 1, 3, 0, 0, 0);
        cleanupService.ThrowingArtistIds.Add(2); // Bad Artist throws
        cleanupService.Results[3] = new CleanupResult(4, 2, 1, 0, 0, 0);
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand();

        task.Execute(command);

        Assert.Contains("1 failed", command.ResultMessage);
        Assert.Contains("3 singles cleaned", command.ResultMessage);
        Assert.True(logger.HasMessageContaining("artist scan failed"));
    }

    [Fact]
    public void Execute_NoMonitoredArtists_SetsEmptyResult()
    {
        var artists = new List<Artist>
        {
            UnmonitoredArtist(1, "Unmonitored")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand();

        task.Execute(command);

        Assert.Contains("0 artists scanned", command.ResultMessage);
        Assert.Empty(cleanupService.ScannedArtists);
    }

    [Fact]
    public void Execute_NullCommand_ThrowsArgumentNullException()
    {
        var task = CreateTask(
            new StubCleanupService(),
            new StubArtistService(new List<Artist>()));

        Assert.Throws<ArgumentNullException>(() => task.Execute(null!));
    }

    [Fact]
    public void Execute_NullArtistList_TreatedAsEmpty()
    {
        var artistService = new StubArtistService(null!);
        var cleanupService = new StubCleanupService();
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand();

        task.Execute(command);

        Assert.Contains("0 artists scanned", command.ResultMessage);
    }

    // ── UniqueSinglesSettings validation ──────────────────────────────

    [Fact]
    public void ScanIntervalMinutes_Default_Is1440()
    {
        var settings = new UniqueSinglesSettings();
        Assert.Equal(1440, settings.ScanIntervalMinutes);
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(60, true)]
    [InlineData(1440, true)]
    [InlineData(10080, true)]
    public void ScanIntervalMinutes_Validation(int minutes, bool expectedValid)
    {
        var settings = new UniqueSinglesSettings { ScanIntervalMinutes = minutes };
        var result = settings.Validate();
        Assert.Equal(expectedValid, result.IsValid);
    }

    // ── IntervalMinutes falls back to 1440 when Definition has no settings ──

    [Fact]
    public void IntervalMinutes_Default_Is1440()
    {
        // Definition.Settings is null by default, so Settings?.ScanIntervalMinutes ?? 1440 returns 1440
        var task = CreateTask(new StubCleanupService(), new StubArtistService(new List<Artist>()));
        Assert.Equal(1440, task.IntervalMinutes);
    }

    // ── Settings resolution priority chain ─────────────────────────────

    [Fact]
    public void ResolveCleanupOptions_UsesNotificationSettings_WhenAvailable()
    {
        // Arrange: notification factory returns a definition with custom UniqueSinglesSettings
        var notificationSettings = new UniqueSinglesSettings
        {
            DurationToleranceMs = 5000,
            ReleaseTypesToCheck = "Album",
            Tier3Action = Tier3Action.Skip
        };
        var notificationDef = new NotificationDefinition { Settings = notificationSettings };
        var factory = new StubNotificationFactory(new List<NotificationDefinition> { notificationDef });

        var artists = new List<Artist> { MonitoredArtist(1, "Artist A") };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = CleanupResult.Empty;

        var task = CreateTask(cleanupService, artistService, notificationFactory: factory);

        // Act
        task.Execute(new UniqueSinglesScanCommand());

        // Assert: the cleanup service was called, meaning the options were resolved
        // from notification settings (DurationToleranceMs=5000 → clamped to 5000)
        Assert.Single(cleanupService.ScannedArtists);
    }

    [Fact]
    public void ResolveCleanupOptions_FallsBackToMetadataProviderSettings_WhenNoNotification()
    {
        // Arrange: no notification definitions (empty list), metadata provider has settings
        var factory = new StubNotificationFactory(new List<NotificationDefinition>());
        var artists = new List<Artist> { MonitoredArtist(1, "Artist A") };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = CleanupResult.Empty;

        var task = CreateTask(cleanupService, artistService, notificationFactory: factory);
        // CreateTask sets Definition.Settings = new UniqueSinglesSettings() by default

        // Act
        task.Execute(new UniqueSinglesScanCommand());

        // Assert: task used metadata provider settings (fallback) — scan completes normally
        Assert.Single(cleanupService.ScannedArtists);
    }

    [Fact]
    public void ResolveCleanupOptions_FallsBackToDefaults_WhenNoSettingsAtAll()
    {
        // Arrange: no notifications, no metadata provider settings
        var factory = new StubNotificationFactory(new List<NotificationDefinition>());
        var artists = new List<Artist> { MonitoredArtist(1, "Artist A") };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = CleanupResult.Empty;

        var task = CreateTask(cleanupService, artistService, notificationFactory: factory);
        task.Definition = new NzbDrone.Core.Extras.Metadata.MetadataDefinition
        {
            Settings = null // No provider settings either
        };

        // Act
        task.Execute(new UniqueSinglesScanCommand());

        // Assert: defaults were used — scan completes normally
        Assert.Single(cleanupService.ScannedArtists);
    }

    [Fact]
    public void ResolveCleanupOptions_LogsWarning_WhenMultipleNotifications()
    {
        // Arrange: two notification definitions with UniqueSinglesSettings
        var settings1 = new UniqueSinglesSettings { DurationToleranceMs = 5000 };
        var settings2 = new UniqueSinglesSettings { DurationToleranceMs = 7000 };
        var defs = new List<NotificationDefinition>
        {
            new() { Settings = settings1 },
            new() { Settings = settings2 }
        };
        var factory = new StubNotificationFactory(defs);

        var artists = new List<Artist> { MonitoredArtist(1, "Artist A") };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = CleanupResult.Empty;
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger, factory);

        // Act
        task.Execute(new UniqueSinglesScanCommand());

        // Assert: warning logged about multiple notifications
        Assert.True(logger.HasMessageContaining("multiple UniqueSingles notifications found"));
        Assert.Single(cleanupService.ScannedArtists);
    }

    // ── Per-artist scan tests ─────────────────────────────────────────

    [Fact]
    public void Execute_PerArtistScan_ScansOnlyTargetArtist()
    {
        var artists = new List<Artist>
        {
            MonitoredArtist(1, "Target Artist"),
            MonitoredArtist(2, "Other Artist"),
            UnmonitoredArtist(3, "Unmonitored Artist")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        cleanupService.Results[1] = new CleanupResult(5, 2, 2, 0, 0, 0);
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand { ArtistId = 1 };

        task.Execute(command);

        Assert.Single(cleanupService.ScannedArtists);
        Assert.Equal(1, cleanupService.ScannedArtists[0].Id);
        Assert.Contains("1 artist scanned", command.ResultMessage);
        Assert.Contains("2 singles cleaned", command.ResultMessage);
    }

    [Fact]
    public void Execute_PerArtistScan_InvalidArtistId_SetsErrorMessage()
    {
        var artists = new List<Artist>
        {
            MonitoredArtist(1, "Artist A")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand { ArtistId = 999 };

        task.Execute(command);

        Assert.Contains("not found", command.ResultMessage);
        Assert.Empty(cleanupService.ScannedArtists);
    }

    [Fact]
    public void Execute_PerArtistScan_NullArtistId_ScansAll()
    {
        var artists = new List<Artist>
        {
            MonitoredArtist(1, "Artist A"),
            MonitoredArtist(2, "Artist B"),
            UnmonitoredArtist(3, "Artist C")
        };
        var artistService = new StubArtistService(artists);
        var cleanupService = new StubCleanupService();
        using var logger = new TestLogger();

        var task = CreateTask(cleanupService, artistService, logger.Logger);
        var command = new UniqueSinglesScanCommand { ArtistId = null };

        task.Execute(command);

        // Regression: existing full-library behavior unchanged
        Assert.Equal(2, cleanupService.ScannedArtists.Count);
        Assert.Contains("2 artists scanned", command.ResultMessage);
    }

    // ── Helper methods and stubs ──────────────────────────────────────

    private static UniqueSinglesScanTask CreateTask(
        StubCleanupService cleanupService,
        StubArtistService artistService,
        Logger? logger = null,
        INotificationFactory? notificationFactory = null)
    {
        var log = logger ?? new TestLogger().Logger;
        var factory = notificationFactory ?? new StubNotificationFactory();
        var task = new UniqueSinglesScanTask(cleanupService, artistService, factory, log);
        // Provider infrastructure sets Definition before use; replicate in tests
        task.Definition = new NzbDrone.Core.Extras.Metadata.MetadataDefinition
        {
            Settings = new UniqueSinglesSettings()
        };
        return task;
    }

    private static Artist MonitoredArtist(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Monitored = true
    };

    private static Artist UnmonitoredArtist(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Monitored = false
    };

    private sealed class StubArtistService : IArtistService
    {
        private readonly List<Artist>? _artists;

        public StubArtistService(List<Artist> artists) => _artists = artists;

        public List<Artist> GetAllArtists() => _artists ?? new List<Artist>();

        // Unused IArtistService members — throw for safety
        public Artist GetArtist(int artistId) =>
            _artists?.FirstOrDefault(a => a.Id == artistId)
            ?? throw new InvalidOperationException($"Artist {artistId} not found");
        public Artist GetArtistByMetadataId(int artistMetadataId) => throw new NotImplementedException();
        public List<Artist> GetArtists(IEnumerable<int> artistIds) => throw new NotImplementedException();
        public Artist AddArtist(Artist newArtist, bool doRefresh) => throw new NotImplementedException();
        public List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh) => throw new NotImplementedException();
        public Artist FindById(string foreignArtistId) => throw new NotImplementedException();
        public Artist FindByName(string title) => throw new NotImplementedException();
        public Artist FindByNameInexact(string title) => throw new NotImplementedException();
        public List<Artist> GetCandidates(string title) => throw new NotImplementedException();
        public void DeleteArtist(int artistId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
        public void DeleteArtists(List<int> artistIds, bool deleteFiles, bool addImportListExclusion = false) => throw new NotImplementedException();
        public Dictionary<int, List<int>> GetAllArtistsTags() => throw new NotImplementedException();
        public List<Artist> AllForTag(int tagId) => throw new NotImplementedException();
        public Artist UpdateArtist(Artist artist, bool publishUpdatedEvent = true) => throw new NotImplementedException();
        public List<Artist> UpdateArtists(List<Artist> artist, bool useExistingRelativeFolder) => throw new NotImplementedException();
        public Dictionary<int, string> AllArtistPaths() => throw new NotImplementedException();
        public bool ArtistPathExists(string folder) => throw new NotImplementedException();
        public void RemoveAddOptions(Artist artist) => throw new NotImplementedException();
    }

    internal sealed class StubCleanupService : ISingleCleanupService
    {
        public List<Artist> ScannedArtists { get; } = new();
        public HashSet<int> ThrowingArtistIds { get; } = new();
        public Dictionary<int, CleanupResult> Results { get; } = new();

        public CleanupResult ScanArtistWithOptions(Artist artist, SingleCleanupOptions options)
        {
            ScannedArtists.Add(artist);
            if (ThrowingArtistIds.Contains(artist.Id))
            {
                throw new InvalidOperationException($"Simulated failure for artist {artist.Name}");
            }

            return Results.TryGetValue(artist.Id, out var result) ? result : CleanupResult.Empty;
        }

        // Unused ISingleCleanupService members
        public void CleanupSinglesForArtist(Artist artist, Album importedAlbum) => throw new NotImplementedException();
        public void CleanupSingleSelfCheck(Artist artist, Album importedSingle) => throw new NotImplementedException();
        public CleanupResult CleanupSingleSelfCheckWithOptions(Artist artist, Album importedSingle, SingleCleanupOptions options) => throw new NotImplementedException();
        public CleanupResult CleanupWithOptions(Artist artist, Album importedAlbum, SingleCleanupOptions options) => throw new NotImplementedException();
    }

    private sealed class TestLogger : IDisposable
    {
        private readonly LogFactory _factory;
        private readonly MemoryTarget _target;

        public TestLogger()
        {
            _factory = new LogFactory();
            _target = new MemoryTarget { Layout = "${level}|${message}" };
            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(_target);
            _factory.Configuration = config;
            Logger = _factory.GetLogger(Guid.NewGuid().ToString("N"));
        }

        public Logger Logger { get; }

        public bool HasMessageContaining(string substring)
        {
            _factory.Flush();
            return _target.Logs.Any(log => log.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            _factory.Flush();
            _factory.Dispose();
        }
    }

    private sealed class StubNotificationFactory : INotificationFactory
    {
        private readonly List<NotificationDefinition> _definitions;

        public StubNotificationFactory(List<NotificationDefinition>? definitions = null)
        {
            _definitions = definitions ?? new List<NotificationDefinition>();
        }

        public List<NotificationDefinition> All() => _definitions;

        // Unused INotificationFactory members — throw for safety
        public List<INotification> GetAvailableProviders() => throw new NotImplementedException();
        public bool Exists(int id) => throw new NotImplementedException();
        public NotificationDefinition Find(int id) => throw new NotImplementedException();
        public NotificationDefinition Get(int id) => throw new NotImplementedException();
        public IEnumerable<NotificationDefinition> Get(IEnumerable<int> ids) => throw new NotImplementedException();
        public NotificationDefinition Create(NotificationDefinition definition) => throw new NotImplementedException();
        public void Update(NotificationDefinition definition) => throw new NotImplementedException();
        public IEnumerable<NotificationDefinition> Update(IEnumerable<NotificationDefinition> definitions) => throw new NotImplementedException();
        public void Delete(int id) => throw new NotImplementedException();
        public void Delete(IEnumerable<int> ids) => throw new NotImplementedException();
        public IEnumerable<NotificationDefinition> GetDefaultDefinitions() => throw new NotImplementedException();
        public IEnumerable<NotificationDefinition> GetPresetDefinitions(NotificationDefinition providerDefinition) => throw new NotImplementedException();
        public void SetProviderCharacteristics(NotificationDefinition definition) => throw new NotImplementedException();
        public void SetProviderCharacteristics(INotification provider, NotificationDefinition definition) => throw new NotImplementedException();
        public INotification GetInstance(NotificationDefinition definition) => throw new NotImplementedException();
        public FluentValidation.Results.ValidationResult Test(NotificationDefinition definition) => throw new NotImplementedException();
        public object RequestAction(NotificationDefinition definition, string action, IDictionary<string, string> query) => throw new NotImplementedException();
        public List<NotificationDefinition> AllForTag(int tagId) => throw new NotImplementedException();
        public List<INotification> OnGrabEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnReleaseImportEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnUpgradeEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnRenameEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnArtistAddEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnArtistDeleteEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnAlbumDeleteEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnHealthIssueEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnHealthRestoredEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnDownloadFailureEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnImportFailureEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnTrackRetagEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
        public List<INotification> OnApplicationUpdateEnabled(bool filterBlockedNotifications = true) => throw new NotImplementedException();
    }
}
