using System.Collections.Generic;
using System.Linq;
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
        var notification = new UniqueSinglesNotification(new RecordingImportCoordinator(), loggerHarness.Logger);

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

    [Fact]
    public void OnReleaseImport_DefinitionSettings_ResolveIntoCoordinatorOptions()
    {
        using var loggerHarness = new LoggerHarness();
        var coordinator = new RecordingImportCoordinator();
        var notification = new UniqueSinglesNotification(coordinator, loggerHarness.Logger)
        {
            Definition = new NotificationDefinition
            {
                Settings = new UniqueSinglesSettings
                {
                    DurationToleranceMs = 4500,
                    ReleaseTypesToCheck = "Album, Single",
                    Tier3Action = Tier3Action.Skip,
                },
            },
        };
        var artist = new Artist { Id = 10, Name = "Artist" };
        var album = new Album { Id = 20, Title = "Release", AlbumType = "Album" };

        notification.OnReleaseImport(Message(artist, album));

        Assert.Equal(1, coordinator.HandleCalls);
        Assert.Same(artist, coordinator.LastMessage?.Artist);
        Assert.Same(album, coordinator.LastMessage?.Album);
        Assert.NotNull(coordinator.LastOptions);
        Assert.Equal(4500, coordinator.LastOptions!.DurationToleranceMs);
        Assert.True(coordinator.LastOptions.ShouldCompareAgainstType("Album"));
        Assert.True(coordinator.LastOptions.ShouldCompareAgainstType("Single"));
        Assert.False(coordinator.LastOptions.ShouldCompareAgainstType("EP"));
        Assert.Equal(Tier3Action.Skip, coordinator.LastOptions.Tier3Action);
    }

    [Fact]
    public void OnReleaseImport_MissingDefinition_UsesSafeDefaultSettings()
    {
        using var loggerHarness = new LoggerHarness();
        var coordinator = new RecordingImportCoordinator();
        var notification = new UniqueSinglesNotification(coordinator, loggerHarness.Logger);

        notification.OnReleaseImport(Message(
            new Artist { Id = 10, Name = "Artist" },
            new Album { Id = 20, Title = "Release", AlbumType = "Single" }));

        Assert.Equal(1, coordinator.HandleCalls);
        Assert.NotNull(coordinator.LastOptions);
        Assert.Equal(3000, coordinator.LastOptions!.DurationToleranceMs);
        Assert.True(coordinator.LastOptions.ShouldCompareAgainstType("Album"));
        Assert.True(coordinator.LastOptions.ShouldCompareAgainstType("EP"));
        Assert.False(coordinator.LastOptions.ShouldCompareAgainstType("Single"));
        Assert.Equal(Tier3Action.FlagOnly, coordinator.LastOptions.Tier3Action);
    }

    [Fact]
    public void OnReleaseImport_NullMessage_DoesNotThrowOrCallCoordinator()
    {
        using var loggerHarness = new LoggerHarness();
        var coordinator = new RecordingImportCoordinator();
        var notification = new UniqueSinglesNotification(coordinator, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(null!));

        Assert.Null(exception);
        Assert.Equal(0, coordinator.HandleCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("null-message", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_NullArtist_DoesNotThrowOrCallCoordinator()
    {
        using var loggerHarness = new LoggerHarness();
        var coordinator = new RecordingImportCoordinator();
        var notification = new UniqueSinglesNotification(coordinator, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = null,
            Album = new Album { Id = 20, Title = "Release", AlbumType = "Album" },
        }));

        Assert.Null(exception);
        Assert.Equal(0, coordinator.HandleCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("null-artist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_NullAlbum_DoesNotThrowOrCallCoordinator()
    {
        using var loggerHarness = new LoggerHarness();
        var coordinator = new RecordingImportCoordinator();
        var notification = new UniqueSinglesNotification(coordinator, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = new Artist { Id = 10, Name = "Artist" },
            Album = null,
        }));

        Assert.Null(exception);
        Assert.Equal(0, coordinator.HandleCalls);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("null-album", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnReleaseImport_CoordinatorException_IsLoggedAndSwallowed()
    {
        using var loggerHarness = new LoggerHarness();
        var coordinator = new RecordingImportCoordinator { ThrowOnHandle = true };
        var notification = new UniqueSinglesNotification(coordinator, loggerHarness.Logger);

        var exception = Record.Exception(() => notification.OnReleaseImport(Message(
            new Artist { Id = 10, Name = "Artist" },
            new Album { Id = 20, Title = "Release", AlbumType = "Album" })));

        Assert.Null(exception);
        Assert.Equal(1, coordinator.HandleCalls);
        Assert.Single(loggerHarness.Entries.Where(entry => entry.StartsWith("Warn|", StringComparison.Ordinal)));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("release-import-handler-failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public void Test_ReturnsSuccessfulValidationResult()
    {
        using var loggerHarness = new LoggerHarness();
        var notification = new UniqueSinglesNotification(new RecordingImportCoordinator(), loggerHarness.Logger);

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

    private sealed class RecordingImportCoordinator : IUniqueSinglesImportCoordinator
    {
        public int HandleCalls { get; private set; }
        public AlbumDownloadMessage? LastMessage { get; private set; }
        public SingleCleanupOptions? LastOptions { get; private set; }
        public bool ThrowOnHandle { get; set; }

        public CleanupResult HandleImport(AlbumDownloadMessage message, SingleCleanupOptions options)
        {
            HandleCalls++;
            LastMessage = message;
            LastOptions = options;

            if (ThrowOnHandle)
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
