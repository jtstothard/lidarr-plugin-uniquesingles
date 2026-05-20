using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using AlbumDownloadMessage = NzbDrone.Core.Notifications.AlbumDownloadMessage;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Routes Lidarr release-import notifications into the appropriate Unique Singles cleanup path
/// and emits top-level route/result summaries for later runtime verification.
/// </summary>
public interface IUniqueSinglesImportCoordinator
{
    CleanupResult HandleImport(AlbumDownloadMessage message, SingleCleanupOptions options);
}

public class UniqueSinglesImportCoordinator : IUniqueSinglesImportCoordinator
{
    private static readonly StringComparer AlbumTypeComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ISingleCleanupService _cleanupService;
    private readonly Logger _logger;

    public UniqueSinglesImportCoordinator(ISingleCleanupService cleanupService, Logger logger)
    {
        _cleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CleanupResult HandleImport(AlbumDownloadMessage message, SingleCleanupOptions options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var artist = message.Artist ?? throw new ArgumentException("Release import message artist was null.", nameof(message));
        var album = message.Album ?? throw new ArgumentException("Release import message album was null.", nameof(message));

        if (IsAlbumOrEp(album.AlbumType))
        {
            const string route = "artist-cleanup";
            LogRoute(route, artist, album, options);
            var result = _cleanupService.CleanupWithOptions(artist, album, options);
            LogResult(route, artist, album, result);
            return result;
        }

        if (IsSingle(album.AlbumType))
        {
            const string route = "single-self-check";
            LogRoute(route, artist, album, options);
            var result = _cleanupService.CleanupSingleSelfCheckWithOptions(artist, album, options);
            LogResult(route, artist, album, result);
            return result;
        }

        const string noOpRoute = "no-op";
        _logger.Info(
            "UniqueSingles import no-op: route={0} artistId={1} artist='{2}' albumId={3} album='{4}' albumType='{5}' reason=unsupported-import-type",
            noOpRoute,
            artist.Id,
            artist.Name,
            album.Id,
            album.Title,
            album.AlbumType);

        LogResult(noOpRoute, artist, album, CleanupResult.Empty);
        return CleanupResult.Empty;
    }

    private void LogRoute(string route, Artist artist, Album album, SingleCleanupOptions options)
    {
        _logger.Info(
            "UniqueSingles import route: route={0} artistId={1} artist='{2}' albumId={3} album='{4}' albumType='{5}' comparisonReleaseTypes='{6}' durationToleranceMs={7} tier3Action='{8}'",
            route,
            artist.Id,
            artist.Name,
            album.Id,
            album.Title,
            album.AlbumType,
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)),
            options.DurationToleranceMs,
            options.Tier3Action);
    }

    private void LogResult(string route, Artist artist, Album album, CleanupResult result)
    {
        _logger.Info(
            "UniqueSingles import result: route={0} artistId={1} artist='{2}' albumId={3} album='{4}' albumType='{5}' candidatesChecked={6} cleaned={7} skipped={8} reviewNeeded={9} unmonitorFailures={10} deleteFailures={11}",
            route,
            artist.Id,
            artist.Name,
            album.Id,
            album.Title,
            album.AlbumType,
            result.CandidatesChecked,
            result.Cleaned,
            result.Skipped,
            result.ReviewNeeded,
            result.UnmonitorFailures,
            result.DeleteFailures);
    }

    private static bool IsAlbumOrEp(string? albumType)
    {
        return AlbumTypeComparer.Equals(albumType, "Album") || AlbumTypeComparer.Equals(albumType, "EP");
    }

    private static bool IsSingle(string? albumType)
    {
        return AlbumTypeComparer.Equals(albumType, "Single");
    }
}
