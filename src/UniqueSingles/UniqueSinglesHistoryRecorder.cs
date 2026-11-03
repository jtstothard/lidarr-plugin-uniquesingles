using System;
using NLog;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Records plugin unmonitor actions as Lidarr History rows so cleanups are auditable outside
/// plugin logs. File deletions already produce Lidarr's own TrackFileDeleted history rows: the
/// plugin deletes through IDeleteMediaFiles, whose MediaFileDeletionService → MediaFileService
/// publishes TrackFileDeletedEvent and Lidarr's EntityHistoryService records it. Unmonitor has no
/// Lidarr-native history event, so this recorder writes album-level rows attributed to the plugin.
/// A recording failure is logged and never affects the cleanup decision.
/// </summary>
public interface ICleanupHistoryRecorder
{
    void RecordUnmonitor(Artist artist, Album single, string reason);
}

public sealed class UniqueSinglesHistoryRecorder : ICleanupHistoryRecorder
{
    private readonly IHistoryRepository _history;
    private readonly Logger _logger;

    public UniqueSinglesHistoryRecorder(IHistoryRepository history, Logger logger)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RecordUnmonitor(Artist artist, Album single, string reason)
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
                SourceTitle = "UniqueSingles",
                ArtistId = artist.Id,
                AlbumId = single.Id,
            };
            history.Data.Add("Plugin", "UniqueSingles");
            history.Data.Add("Action", "Unmonitor");
            history.Data.Add("Reason", reason ?? string.Empty);
            _history.Insert(history);
            _logger.Info(
                "UniqueSingles history: action=unmonitor-recorded artistId={0} artist='{1}' albumId={2} album='{3}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles history: action=unmonitor-record-failed artistId={0} albumId={1} reason=history-insert-failed",
                artist.Id,
                single.Id);
        }
    }
}
