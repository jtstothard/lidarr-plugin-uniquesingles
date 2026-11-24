using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Read-only check for whether Lidarr can still import downloaded files into a release.
/// A file-less single is only unmonitored when this returns false: a single that is already
/// grabbed, downloading, or awaiting import is file-less at that moment, and unmonitoring it
/// would let the download land in an unmonitored release.
/// </summary>
public interface IInFlightReleaseProbe
{
    /// <summary>
    /// Returns true when <paramref name="release"/> has a grabbed, downloading, or
    /// awaiting-import download, or when that cannot be ruled out (lookup failure).
    /// </summary>
    bool IsInFlight(Album release, out string detail);
}

/// <summary>
/// Answers <see cref="IInFlightReleaseProbe"/> from Lidarr's own state without issuing commands:
/// <list type="bullet">
/// <item>The tracked-download cache (the data behind Activity → Queue) is authoritative for any
/// download Lidarr is tracking. Every state except the terminal ones (Imported, Ignored,
/// DownloadFailed, DownloadFailedPending) can still import files into the release.</item>
/// <item>A Grabbed history row covers the gap before the download is tracked: Lidarr writes the
/// row synchronously at grab time, but only adds the download to the tracked cache on its next
/// debounced RefreshMonitoredDownloads, and the cache starts empty after a restart. A grab
/// counts while it is not tracked, has no DownloadImported/DownloadFailed/DownloadIgnored row,
/// and is newer than <see cref="UntrackedGrabWindow"/>, so a download removed from the client
/// outside Lidarr only defers the unmonitor instead of blocking it for good.</item>
/// </list>
/// Delay-profile pending releases are deliberately not counted: they are not grabbed yet, and
/// RSS sync re-evaluates them with MonitoredAlbumSpecification, so unmonitoring drops them.
/// Lookup failures fail safe and report the release as in flight.
/// </summary>
public sealed class LidarrInFlightReleaseProbe : IInFlightReleaseProbe
{
    internal static readonly TimeSpan UntrackedGrabWindow = TimeSpan.FromHours(6);

    private static readonly HashSet<TrackedDownloadState> TerminalStates = new()
    {
        TrackedDownloadState.Imported,
        TrackedDownloadState.Ignored,
        TrackedDownloadState.DownloadFailed,
        TrackedDownloadState.DownloadFailedPending,
    };

    private static readonly HashSet<EntityHistoryEventType> TerminalHistoryEvents = new()
    {
        EntityHistoryEventType.DownloadImported,
        EntityHistoryEventType.DownloadFailed,
        EntityHistoryEventType.DownloadIgnored,
    };

    private readonly Lazy<ITrackedDownloadService> _trackedDownloads;
    private readonly Lazy<IHistoryService> _history;
    private readonly Func<DateTime> _utcNow;
    private readonly Logger _logger;

    /// <summary>
    /// Lidarr services are resolved lazily for the same recursive DryIoc dependency reasons as
    /// the notification/metadata factories in EagerSingleUnmonitorService.
    /// </summary>
    public LidarrInFlightReleaseProbe(Lazy<ITrackedDownloadService> trackedDownloads, Lazy<IHistoryService> history, Logger logger)
        : this(trackedDownloads, history, () => DateTime.UtcNow, logger)
    {
    }

    internal LidarrInFlightReleaseProbe(Lazy<ITrackedDownloadService> trackedDownloads, Lazy<IHistoryService> history, Func<DateTime> utcNow, Logger logger)
    {
        _trackedDownloads = trackedDownloads ?? throw new ArgumentNullException(nameof(trackedDownloads));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsInFlight(Album release, out string detail)
    {
        if (release == null)
        {
            detail = "no-release";
            return false;
        }

        try
        {
            var trackedDownloads = _trackedDownloads.Value;
            foreach (var tracked in trackedDownloads.GetTrackedDownloads() ?? new List<TrackedDownload>())
            {
                if (tracked?.RemoteAlbum?.Albums != null
                    && tracked.RemoteAlbum.Albums.Any(a => a != null && a.Id == release.Id)
                    && !TerminalStates.Contains(tracked.State))
                {
                    detail = $"tracked-download state={tracked.State} downloadId={tracked.DownloadItem?.DownloadId}";
                    return true;
                }
            }

            var cutoff = _utcNow() - UntrackedGrabWindow;
            var grabs = _history.Value.GetByAlbum(release.Id, EntityHistoryEventType.Grabbed) ?? new List<EntityHistory>();
            foreach (var grab in grabs.Where(g => g != null && !string.IsNullOrEmpty(g.DownloadId)))
            {
                var tracked = trackedDownloads.Find(grab.DownloadId);
                if (tracked != null)
                {
                    // Tracked state is authoritative even when the parsed release mapping
                    // above did not resolve to this album.
                    if (!TerminalStates.Contains(tracked.State))
                    {
                        detail = $"tracked-download state={tracked.State} downloadId={grab.DownloadId}";
                        return true;
                    }

                    continue;
                }

                if (grab.Date < cutoff)
                {
                    continue;
                }

                var finished = (_history.Value.FindByDownloadId(grab.DownloadId) ?? new List<EntityHistory>())
                    .Any(h => h != null && TerminalHistoryEvents.Contains(h.EventType));
                if (!finished)
                {
                    detail = $"recent-grab-not-yet-tracked downloadId={grab.DownloadId} grabbedAt={grab.Date:u}";
                    return true;
                }
            }

            detail = "none";
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles in-flight check failed; treating release as in flight. albumId={0} album='{1}' reason=in-flight-lookup-failed",
                release.Id,
                release.Title);
            detail = "lookup-failed";
            return true;
        }
    }
}
