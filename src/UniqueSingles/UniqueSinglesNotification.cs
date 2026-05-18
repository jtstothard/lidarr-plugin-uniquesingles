using FluentValidation.Results;
using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;

namespace UniqueSingles;

/// <summary>
/// Lidarr Connection notification entry point for Unique Singles release-import handling.
/// The notification is intentionally only a routing and exception-containment boundary;
/// cleanup decisions are delegated to SingleCleanupService.
/// </summary>
public class UniqueSinglesNotification : NotificationBase<UniqueSinglesSettings>
{
    private static readonly StringComparer AlbumTypeComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ISingleCleanupService _cleanupService;
    private readonly Logger _logger;

    public UniqueSinglesNotification(
        IAlbumService albumService,
        ITrackService trackService,
        IMediaFileService mediaFileService,
        IDeleteMediaFiles deleteMediaFiles,
        Logger logger)
        : this(new SingleCleanupService(albumService, trackService, mediaFileService, deleteMediaFiles, logger), logger)
    {
    }

    internal UniqueSinglesNotification(ISingleCleanupService cleanupService, Logger logger)
    {
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override string Name => Plugin.Name;

    public override ValidationFailure? Test()
    {
        return null;
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

            if (AlbumTypeComparer.Equals(album.AlbumType, "Album") || AlbumTypeComparer.Equals(album.AlbumType, "EP"))
            {
                _logger.Info(
                    "UniqueSingles import route: album cleanup. artistId={0} artist='{1}' albumId={2} album='{3}' albumType='{4}'",
                    artist.Id,
                    artist.Name,
                    album.Id,
                    album.Title,
                    album.AlbumType);
                _cleanupService.CleanupSinglesForArtist(artist, album);
                return;
            }

            if (AlbumTypeComparer.Equals(album.AlbumType, "Single"))
            {
                _logger.Info(
                    "UniqueSingles import route: single self-check. artistId={0} artist='{1}' albumId={2} album='{3}' albumType='{4}'",
                    artist.Id,
                    artist.Name,
                    album.Id,
                    album.Title,
                    album.AlbumType);
                _cleanupService.CleanupSingleSelfCheck(artist, album);
                return;
            }

            _logger.Info(
                "UniqueSingles import no-op: unsupported release type. artistId={0} artist='{1}' albumId={2} album='{3}' albumType='{4}' reason=unsupported-import-type",
                artist.Id,
                artist.Name,
                album.Id,
                album.Title,
                album.AlbumType);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "UniqueSingles import handler swallowed exception. reason=release-import-handler-failed");
        }
    }
}
