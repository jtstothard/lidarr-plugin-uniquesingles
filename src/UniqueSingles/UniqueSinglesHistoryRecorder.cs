using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Who last changed a release's monitored state, as far as the plugin's own History rows tell.
/// </summary>
public enum UnmonitorProvenance
{
    /// <summary>No plugin row: any unmonitored state was set by the user or Lidarr.</summary>
    None,

    /// <summary>The latest plugin row is an unmonitor the plugin applied as redundant.</summary>
    PluginUnmonitored,

    /// <summary>The user explicitly re-monitored the release after the plugin unmonitored it.</summary>
    UserRemonitored,

    /// <summary>The History lookup failed; callers must not act on the release.</summary>
    Unknown,
}

public static class UnmonitorProvenanceExtensions
{
    /// <summary>
    /// True when the plugin must leave the release alone: the user re-monitored it, provenance
    /// cannot be read, or the release is monitored now although the plugin's latest row is an
    /// Unmonitor. The plugin never re-monitors a release, so a monitored release with a standing
    /// plugin Unmonitor row was taken back by the user and its UserRemonitor row is missing: the
    /// write failed, the artist lookup threw, or Lidarr's orphaned-history housekeeping removed a
    /// row keyed to no artist. Without this, the next scan would unmonitor it again and delete
    /// its files.
    /// </summary>
    public static bool ProtectsRelease(this UnmonitorProvenance provenance, bool monitored)
    {
        return provenance == UnmonitorProvenance.UserRemonitored
            || provenance == UnmonitorProvenance.Unknown
            || (monitored && provenance == UnmonitorProvenance.PluginUnmonitored);
    }
}

/// <summary>
/// Records plugin unmonitor actions as Lidarr History rows so cleanups are auditable outside
/// plugin logs. File deletions already produce Lidarr's own TrackFileDeleted history rows: the
/// plugin deletes through IDeleteMediaFiles, whose MediaFileDeletionService → MediaFileService
/// publishes TrackFileDeletedEvent and Lidarr's EntityHistoryService records it. Unmonitor has no
/// Lidarr-native history event, so this recorder writes album-level rows attributed to the plugin.
/// Recording never throws; the Record* methods report whether the row was written.
///
/// The same rows are the plugin's provenance record: an unmonitored release is only treated as
/// plugin-unmonitored when its latest plugin row is an Unmonitor, and a UserRemonitor row marks
/// a release the user took back. Such a release, and a monitored release whose latest plugin row
/// is still an Unmonitor (see <see cref="UnmonitorProvenanceExtensions.ProtectsRelease"/>), is
/// not unmonitored or deleted by the plugin while that row stands. Because a
/// file-less unmonitor relies on its row (without it, a download landing later is never
/// cleaned), callers write the Unmonitor row before unmonitoring and skip a file-less unmonitor
/// whose row could not be written.
/// </summary>
public interface ICleanupHistoryRecorder
{
    /// <summary>Writes an Unmonitor row. Returns false when the row could not be written.</summary>
    bool RecordUnmonitor(Artist artist, Album single, string reason);

    /// <summary>
    /// Supersedes an Unmonitor row written ahead of an unmonitor that then failed, so the
    /// still-monitored release is not mistaken for plugin-unmonitored.
    /// </summary>
    bool RecordUnmonitorReverted(Artist artist, Album single);

    /// <summary>
    /// Writes a UserRemonitor row. <paramref name="artist"/> may be null when it cannot be
    /// resolved; the row is keyed by album, so it still protects the release.
    /// </summary>
    bool RecordUserRemonitor(Artist? artist, Album single);

    UnmonitorProvenance GetProvenance(int albumId);

    /// <summary>
    /// Leak-backstop gate for files in a release the plugin unmonitored. True when every file
    /// traces through Lidarr's TrackFileImported row to a Grabbed row that is either automatic
    /// (any ReleaseSource other than InteractiveSearch or UserInvokedSearch, whenever it was
    /// grabbed) or user-initiated and dated at or before the plugin's latest Unmonitor row plus
    /// <see cref="UniqueSinglesHistoryRecorder.GrabUnmonitorTolerance"/>. A user grab after the
    /// unmonitor, an import without a grab (manual or disk-scan), missing rows, or a lookup
    /// failure returns false and the files are kept.
    /// </summary>
    bool FilesCameFromCleanableGrab(int albumId, IReadOnlyCollection<int> trackFileIds, out string detail);
}

public sealed class UniqueSinglesHistoryRecorder : ICleanupHistoryRecorder
{
    internal const string PluginKey = "Plugin";
    internal const string PluginName = "UniqueSingles";
    internal const string ActionKey = "Action";
    internal const string UnmonitorAction = "Unmonitor";
    internal const string UnmonitorRevertedAction = "UnmonitorReverted";
    internal const string UserRemonitorAction = "UserRemonitor";

    // Lidarr's own keys on Grabbed and TrackFileImported rows (EntityHistoryService).
    internal const string ReleaseSourceKey = "ReleaseSource";
    internal const string FileIdKey = "FileId";

    /// <summary>
    /// A user-initiated grab recorded this soon after the unmonitor still counts as predating
    /// it: a search decides on the monitored state it read, then sends each approved release to
    /// the download client in turn, so the Grabbed row can trail the decision. Automatic grabs
    /// are cleaned whatever their timing, so this window only applies to user sources.
    /// </summary>
    internal static readonly TimeSpan GrabUnmonitorTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ReleaseSourceType values (Lidarr NzbDrone.Core.Parser.Model.ReleaseSourceType) that mean
    /// the user started the search: InteractiveSearch is a grab from the interactive-search
    /// results, UserInvokedSearch a manually triggered search command. Every other value (Rss,
    /// Search, ReleasePush, Unknown, a missing or unrecognised value) is treated as automatic.
    /// Lidarr runs automatic searches long after an album is unmonitored (the post-refresh
    /// AlbumSearchCommand for ids captured while monitored, Missing sweeps over a list captured at
    /// sweep start, failed-download re-searches, import-list sync), and its
    /// MonitoredAlbumSpecification does not check the monitored flag for searches, so an
    /// automatic grab after the unmonitor is still a leak, not a user fetch.
    /// </summary>
    private static readonly HashSet<string> UserReleaseSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "InteractiveSearch",
        "UserInvokedSearch",
    };

    private readonly IHistoryRepository _history;
    private readonly Logger _logger;

    public UniqueSinglesHistoryRecorder(IHistoryRepository history, Logger logger)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool RecordUnmonitor(Artist artist, Album single, string reason)
    {
        return artist != null && Record(artist, single, UnmonitorAction, reason, "unmonitor");
    }

    public bool RecordUnmonitorReverted(Artist artist, Album single)
    {
        return artist != null && Record(
            artist,
            single,
            UnmonitorRevertedAction,
            "The unmonitor recorded just before this row failed; the release stayed monitored.",
            "unmonitor-reverted");
    }

    public bool RecordUserRemonitor(Artist? artist, Album single)
    {
        return Record(
            artist,
            single,
            UserRemonitorAction,
            "User re-monitored a release UniqueSingles had unmonitored; the plugin leaves it alone while this row stands.",
            "user-remonitor");
    }

    public UnmonitorProvenance GetProvenance(int albumId)
    {
        try
        {
            return ToProvenance(LatestPluginRow(_history.GetByAlbum(albumId, EntityHistoryEventType.Unknown)));
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles history: action=provenance-lookup-failed albumId={0} reason=history-query-failed",
                albumId);
            return UnmonitorProvenance.Unknown;
        }
    }

    public bool FilesCameFromCleanableGrab(int albumId, IReadOnlyCollection<int> trackFileIds, out string detail)
    {
        if (trackFileIds == null || trackFileIds.Count == 0)
        {
            detail = "no-files";
            return false;
        }

        try
        {
            var rows = (_history.GetByAlbum(albumId, null) ?? new List<EntityHistory>())
                .Where(h => h != null)
                .ToList();

            var latestPlugin = LatestPluginRow(rows);
            if (ToProvenance(latestPlugin) != UnmonitorProvenance.PluginUnmonitored)
            {
                detail = "no-plugin-unmonitor-row";
                return false;
            }

            var unmonitoredAt = latestPlugin!.Date;
            var cutoff = unmonitoredAt + GrabUnmonitorTolerance;

            foreach (var fileId in trackFileIds)
            {
                var fileIdText = fileId.ToString(CultureInfo.InvariantCulture);
                var import = rows
                    .Where(h => h.EventType == EntityHistoryEventType.TrackFileImported
                        && string.Equals(GetData(h, FileIdKey), fileIdText, StringComparison.Ordinal))
                    .OrderByDescending(h => h.Date)
                    .ThenByDescending(h => h.Id)
                    .FirstOrDefault();

                if (import == null)
                {
                    // Lidarr writes TrackFileImported only for new downloads; a disk-scan import
                    // (files placed in the artist folder) leaves none.
                    detail = $"fileId={fileId} no-import-row";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(import.DownloadId))
                {
                    // Manual import of files that belong to no grab.
                    detail = $"fileId={fileId} imported-without-download";
                    return false;
                }

                var grab = rows
                    .Where(h => h.EventType == EntityHistoryEventType.Grabbed
                        && string.Equals(h.DownloadId, import.DownloadId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(h => h.Date)
                    .FirstOrDefault();

                if (grab == null)
                {
                    detail = $"fileId={fileId} downloadId={import.DownloadId} no-grab-row";
                    return false;
                }

                // Automatic grabs are cleaned whatever their timing. A user grab is kept when it
                // postdates the unmonitor (the user fetching a single the plugin dropped) and
                // cleaned when it predates it (it raced the unmonitor like any other download).
                var source = GetData(grab, ReleaseSourceKey);
                if (IsUserReleaseSource(source) && grab.Date > cutoff)
                {
                    detail = $"fileId={fileId} downloadId={import.DownloadId} user-grab-after-unmonitor releaseSource={source} grabbedAt={grab.Date:u} unmonitoredAt={unmonitoredAt:u}";
                    return false;
                }
            }

            detail = $"all-files-from-automatic-or-pre-unmonitor-grabs unmonitoredAt={unmonitoredAt:u}";
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles history: action=grab-provenance-lookup-failed albumId={0} reason=history-query-failed",
                albumId);
            detail = "lookup-failed";
            return false;
        }
    }

    internal static bool IsUserReleaseSource(string? releaseSource)
    {
        return releaseSource != null && UserReleaseSources.Contains(releaseSource.Trim());
    }

    private static EntityHistory? LatestPluginRow(IEnumerable<EntityHistory>? rows)
    {
        return (rows ?? Enumerable.Empty<EntityHistory>())
            .Where(IsPluginRow)
            .OrderByDescending(h => h.Date)
            .ThenByDescending(h => h.Id)
            .FirstOrDefault();
    }

    private static UnmonitorProvenance ToProvenance(EntityHistory? latest)
    {
        if (latest == null)
        {
            return UnmonitorProvenance.None;
        }

        var action = GetData(latest, ActionKey);
        if (string.Equals(action, UnmonitorAction, StringComparison.Ordinal))
        {
            return UnmonitorProvenance.PluginUnmonitored;
        }

        if (string.Equals(action, UserRemonitorAction, StringComparison.Ordinal))
        {
            return UnmonitorProvenance.UserRemonitored;
        }

        // UnmonitorReverted (and any unrecognised action) leaves no plugin unmonitor standing.
        return UnmonitorProvenance.None;
    }

    private static bool IsPluginRow(EntityHistory history)
    {
        return history != null && string.Equals(GetData(history, PluginKey), PluginName, StringComparison.Ordinal);
    }

    /// <summary>
    /// History Data is stored as JSON with Lidarr's camelCase dictionary-key policy
    /// (EmbeddedDocumentConverter), so a row written with "Action" reads back from the database
    /// as "action". Keys are matched case-insensitively; in-memory rows keep their original case.
    /// </summary>
    private static string? GetData(EntityHistory history, string key)
    {
        if (history?.Data == null)
        {
            return null;
        }

        if (history.Data.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var entry in history.Data)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Never throws. <paramref name="artist"/> may be null, in which case the row is keyed by the
    /// album's ArtistId, which is read inside the try because it can lazy-load.
    /// </summary>
    private bool Record(Artist? artist, Album single, string action, string reason, string logAction)
    {
        if (single == null)
        {
            return false;
        }

        int? artistId = null;
        try
        {
            artistId = artist?.Id ?? single.ArtistId;
            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.Unknown,
                Date = DateTime.UtcNow,
                Quality = new QualityModel(),
                SourceTitle = PluginName,
                ArtistId = artistId.Value,
                AlbumId = single.Id,
            };
            history.Data.Add(PluginKey, PluginName);
            history.Data.Add(ActionKey, action);
            history.Data.Add("Reason", reason ?? string.Empty);
            _history.Insert(history);
            _logger.Info(
                "UniqueSingles history: action={0}-recorded artistId={1} artist='{2}' albumId={3} album='{4}'",
                logAction,
                artistId,
                artist?.Name,
                single.Id,
                single.Title);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles history: action={0}-record-failed artistId={1} albumId={2} reason=history-insert-failed",
                logAction,
                artistId,
                single.Id);
            return false;
        }
    }
}
