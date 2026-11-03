using System;
using System.Threading;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using AlbumDownloadMessage = NzbDrone.Core.Notifications.AlbumDownloadMessage;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Lidarr Connection notification entry point for Unique Singles release-import handling.
/// The notification is intentionally only a settings-resolution, null-guard, and exception-containment boundary;
/// import routing and aggregate cleanup logging are delegated to UniqueSinglesImportCoordinator.
/// </summary>
public class UniqueSinglesNotification : NotificationBase<UniqueSinglesSettings>
{
    private readonly IUniqueSinglesImportCoordinator _importCoordinator;
    private readonly IAudioAnalysisCancellation _audioAnalysisCancellation;
    private readonly Logger _logger;

    public UniqueSinglesNotification(
        IAlbumService albumService,
        ITrackService trackService,
        IMediaFileService mediaFileService,
        IDeleteMediaFiles deleteMediaFiles,
        ICleanupHistoryRecorder historyRecorder,
        IAudioAnalysisCancellation audioAnalysisCancellation,
        Logger logger)
        : this(new SingleCleanupService(albumService, trackService, mediaFileService, deleteMediaFiles, historyRecorder, logger), audioAnalysisCancellation, logger)
    {
    }

    internal UniqueSinglesNotification(ISingleCleanupService cleanupService, IAudioAnalysisCancellation audioAnalysisCancellation, Logger logger)
        : this(new UniqueSinglesImportCoordinator(cleanupService, logger), audioAnalysisCancellation, logger)
    {
    }

    internal UniqueSinglesNotification(IUniqueSinglesImportCoordinator importCoordinator, IAudioAnalysisCancellation audioAnalysisCancellation, Logger logger)
    {
        _importCoordinator = importCoordinator ?? throw new ArgumentNullException(nameof(importCoordinator));
        _audioAnalysisCancellation = audioAnalysisCancellation ?? throw new ArgumentNullException(nameof(audioAnalysisCancellation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Test-only convenience: an uncancelled cancellation hub, so existing coordinator-level tests
    /// keep constructing the notification without dispatcher plumbing.
    /// </summary>
    internal UniqueSinglesNotification(IUniqueSinglesImportCoordinator importCoordinator, Logger logger)
        : this(importCoordinator, new AudioAnalysisCancellation(logger), logger)
    {
    }

    public override string Name => UniqueSinglesPlugin.DisplayName;

    public override string Link => UniqueSinglesPlugin.RepositoryUrl;

    public override ValidationResult Test()
    {
        return new ValidationResult();
    }

    public override void OnReleaseImport(AlbumDownloadMessage message)
    {
        try
        {
            var artist = message?.Artist;
            var album = message?.Album;

            if (message == null)
            {
                _logger.Info("UniqueSingles import no-op: release import message was null. reason=null-message");
                return;
            }

            if (artist == null)
            {
                _logger.Info(
                    "UniqueSingles import no-op: release import message has no artist. albumId={0} album='{1}' albumType='{2}' reason=null-artist",
                    album?.Id,
                    album?.Title,
                    album?.AlbumType);
                return;
            }

            if (album == null)
            {
                _logger.Info(
                    "UniqueSingles import no-op: release import message has no album. artistId={0} artist='{1}' reason=null-album",
                    artist.Id,
                    artist.Name);
                return;
            }

            // Link the dispatcher's observable lifecycle signal into the audio-analysis option so
            // decoder work stops when the host shuts down. Settings resolution and precedence are
            // unchanged; only the cancellation token is composed.
            var options = ResolveCleanupOptions();
            using var dispatcherCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                options.AudioCancellation, _audioAnalysisCancellation.Token);
            _importCoordinator.HandleImport(message, options.WithAudioCancellation(dispatcherCancellation.Token));
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "UniqueSingles import handler swallowed exception. reason=release-import-handler-failed");
        }
    }

    private SingleCleanupOptions ResolveCleanupOptions()
    {
        var settings = Definition?.Settings as UniqueSinglesSettings ?? new UniqueSinglesSettings();
        return settings.ToCleanupOptions();
    }
}
