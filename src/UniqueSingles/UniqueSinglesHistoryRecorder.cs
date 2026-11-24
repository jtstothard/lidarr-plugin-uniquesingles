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
/// a release the user took back, which the plugin never unmonitors or deletes again. Because a
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
    /// Leak-backstop gate for files in a release the plugin unmonitored. True only when every
    /// file traces through Lidarr's TrackFileImported row to an automatic Grabbed row dated at
    /// or before the plugin's latest Unmonitor row (plus <see cref="UniqueSinglesHistoryRecorder.GrabUnmonitorTolerance"/>):
    /// the download raced the unmonitor. Anything else (a user grab, a grab after the unmonitor,
    /// a manual or disk-scan import without a grab, missing rows, lookup failure) returns false
    /// and the files are kept.
    /// </summary>
    bool FilesCameFromPreUnmonitorGrab(int albumId, IReadOnlyCollection<int> trackFileIds, out string detail);
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
    /// A grab recorded this soon after the unmonitor still counts as racing it: RSS sync decides
    /// on the monitored state it read, then sends each approved release to the download client
    /// in turn, so the Grabbed row can trail the decision. User-started grabs are excluded by
    /// their ReleaseSource, not by this window.
    /// </summary>
    internal static readonly TimeSpan GrabUnmonitorTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ReleaseSourceType values (Lidarr DownloadDecisionMaker) that mean the user started the
    /// search: InteractiveSearch is a grab from the interactive-search results, UserInvokedSearch
    /// a manually triggered album/artist search command. Lidarr accepts unmonitored albums for
    /// both, so they are how a user fetches a single the plugin unmonitored.
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
        return artist != null && Record(artist.Id, artist.Name, single, UnmonitorAction, reason, "unmonitor");
    }

    public bool RecordUnmonitorReverted(Artist artist, Album single)
    {
        return artist != null && Record(
            artist.Id,
            artist.Name,
            single,
            UnmonitorRevertedAction,
            "The unmonitor recorded just before this row failed; the release stayed monitored.",
            "unmonitor-reverted");
    }

    public bool RecordUserRemonitor(Artist? artist, Album single)
    {
        if (single == null)
        {
            return false;
        }

        return Record(
            artist?.Id ?? single.ArtistId,
            artist?.Name,
            single,
            UserRemonitorAction,
            "User re-monitored a release UniqueSingles had unmonitored; the plugin will not unmonitor it or delete its files again.",
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

    public bool FilesCameFromPreUnmonitorGrab(int albumId, IReadOnlyCollection<int> trackFileIds, out string detail)
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

                var source = GetData(grab, ReleaseSourceKey);
                if (source != null && UserReleaseSources.Contains(source))
                {
                    detail = $"fileId={fileId} downloadId={import.DownloadId} user-grab releaseSource={source}";
                    return false;
                }

                if (grab.Date > cutoff)
                {
                    detail = $"fileId={fileId} downloadId={import.DownloadId} grabbed-after-unmonitor grabbedAt={grab.Date:u} unmonitoredAt={unmonitoredAt:u}";
                    return false;
                }
            }

            detail = $"all-files-from-grabs-before-unmonitor unmonitoredAt={unmonitoredAt:u}";
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

    private bool Record(int artistId, string? artistName, Album single, string action, string reason, string logAction)
    {
        if (single == null)
        {
            return false;
        }

        try
        {
            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.Unknown,
                Date = DateTime.UtcNow,
                Quality = new QualityModel(),
                SourceTitle = PluginName,
                ArtistId = artistId,
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
                artistName,
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
