using System;
using System.Collections.Generic;
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
/// A recording failure is logged and never affects the cleanup decision.
///
/// The same rows are the plugin's provenance record: an unmonitored release is only treated as
/// plugin-unmonitored when its latest plugin row is an Unmonitor, and a UserRemonitor row marks
/// a release the user took back, which the plugin never unmonitors or deletes again.
/// </summary>
public interface ICleanupHistoryRecorder
{
    void RecordUnmonitor(Artist artist, Album single, string reason);

    void RecordUserRemonitor(Artist artist, Album single);

    UnmonitorProvenance GetProvenance(int albumId);
}

public sealed class UniqueSinglesHistoryRecorder : ICleanupHistoryRecorder
{
    internal const string PluginKey = "Plugin";
    internal const string PluginName = "UniqueSingles";
    internal const string ActionKey = "Action";
    internal const string UnmonitorAction = "Unmonitor";
    internal const string UserRemonitorAction = "UserRemonitor";

    private readonly IHistoryRepository _history;
    private readonly Logger _logger;

    public UniqueSinglesHistoryRecorder(IHistoryRepository history, Logger logger)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RecordUnmonitor(Artist artist, Album single, string reason)
    {
        Record(artist, single, UnmonitorAction, reason, "unmonitor");
    }

    public void RecordUserRemonitor(Artist artist, Album single)
    {
        Record(
            artist,
            single,
            UserRemonitorAction,
            "User re-monitored a release UniqueSingles had unmonitored; the plugin will not unmonitor it or delete its files again.",
            "user-remonitor");
    }

    public UnmonitorProvenance GetProvenance(int albumId)
    {
        try
        {
            var latest = (_history.GetByAlbum(albumId, EntityHistoryEventType.Unknown) ?? new List<EntityHistory>())
                .Where(IsPluginRow)
                .OrderByDescending(h => h.Date)
                .ThenByDescending(h => h.Id)
                .FirstOrDefault();

            if (latest == null)
            {
                return UnmonitorProvenance.None;
            }

            latest.Data.TryGetValue(ActionKey, out var action);
            return action switch
            {
                UnmonitorAction => UnmonitorProvenance.PluginUnmonitored,
                UserRemonitorAction => UnmonitorProvenance.UserRemonitored,
                _ => UnmonitorProvenance.None,
            };
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

    private static bool IsPluginRow(EntityHistory history)
    {
        return history?.Data != null
            && history.Data.TryGetValue(PluginKey, out var plugin)
            && plugin == PluginName;
    }

    private void Record(Artist artist, Album single, string action, string reason, string logAction)
    {
        if (artist == null || single == null)
        {
            return;
        }

        try
        {
            var history = new EntityHistory
            {
                EventType = EntityHistoryEventType.Unknown,
                Date = DateTime.UtcNow,
                Quality = new QualityModel(),
                SourceTitle = PluginName,
                ArtistId = artist.Id,
                AlbumId = single.Id,
            };
            history.Data.Add(PluginKey, PluginName);
            history.Data.Add(ActionKey, action);
            history.Data.Add("Reason", reason ?? string.Empty);
            _history.Insert(history);
            _logger.Info(
                "UniqueSingles history: action={0}-recorded artistId={1} artist='{2}' albumId={3} album='{4}'",
                logAction,
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles history: action={0}-record-failed artistId={1} albumId={2} reason=history-insert-failed",
                logAction,
                artist.Id,
                single.Id);
        }
    }
}
