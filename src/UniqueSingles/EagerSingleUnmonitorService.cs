using System;
using System.Collections.Generic;
using System.Linq;

using NLog;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Events;
using NzbDrone.Core.Notifications;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Eager pre-grab guard for redundant singles (#26). Runs the exact redundancy decision the
/// cleanup path uses (TrackMatcher.CheckSingle over every catalog track, compared against
/// downloaded tracks on monitored releases of the configured comparison types) at the
/// lifecycle points where a single becomes grabbable — metadata refresh, album add, album
/// refresh, and monitoring edits — so a fully redundant single is unmonitored before RSS or
/// a search can grab it, instead of being downloaded first and cleaned up on import.
///
/// This service is deliberately narrower than the cleanup path in two ways:
/// - It only ever unmonitors; it never deletes files.
/// - It never touches a single that has any downloaded track file. Releases with content
///   remain owned by the import-time and scheduled-scan cleanup paths, whose unmonitor
///   decision is followed by file deletion.
///
/// It reuses the same protections unchanged: #17 whole-release protection (a single with any
/// catalog track lacking a Tier-1/Tier-2 match — downloaded or not — is exclusive content and
/// stays monitored), Tier-3 title-only evidence never unmonitors (FlagOnly semantics), and
/// the comparison pool is the identical monitored Album/EP, non-excluded-secondary-type,
/// downloaded-tracks-only selection the cleanup path applies.
///
/// Lidarr's plugin assembly registration exposes every public class implementing
/// IHandle&lt;TEvent&gt; to the EventAggregator, so these handlers run synchronously inside
/// the album lifecycle operations — strictly before any queued refresh/search command can
/// execute against the new monitoring state. A grab-time hook (NotificationBase.OnGrab) is
/// deliberately NOT used: notifications fire after Lidarr has already sent the download to
/// the client, and unmonitoring there would leak the in-flight file, because the import-time
/// cleanup skips already-unmonitored releases.
/// </summary>
public interface IEagerSingleUnmonitorService
{
    /// <summary>
    /// Unmonitors <paramref name="single"/> when it is a monitored single with no downloaded
    /// files whose every catalog track has a Tier-1/Tier-2 redundant match against the
    /// artist's downloaded library, and the eager gate is enabled. Returns true when the
    /// unmonitor was applied.
    /// </summary>
    bool TryUnmonitorIfRedundant(Artist artist, Album single, string trigger, SingleCleanupOptions options);
}

public class EagerSingleUnmonitorService : IEagerSingleUnmonitorService,
    IHandle<AlbumInfoRefreshedEvent>,
    IHandle<AlbumAddedEvent>,
    IHandle<AlbumUpdatedEvent>,
    IHandle<AlbumEditedEvent>
{
    private static readonly StringComparer AlbumTypeComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IAlbumService _albumService;
    private readonly ITrackService _trackService;
    private readonly IArtistService _artistService;
    private readonly ICleanupHistoryRecorder _historyRecorder;
    private readonly Lazy<INotificationFactory> _notificationFactory;
    private readonly Lazy<IMetadataFactory> _metadataFactory;
    private readonly Logger _logger;

    /// <summary>
    /// INotificationFactory/IMetadataFactory are resolved lazily for the same recursive
    /// DryIoc dependency reasons as UniqueSinglesScanTask.
    /// </summary>
    public EagerSingleUnmonitorService(
        IAlbumService albumService,
        ITrackService trackService,
        IArtistService artistService,
        ICleanupHistoryRecorder historyRecorder,
        Lazy<INotificationFactory> notificationFactory,
        Lazy<IMetadataFactory> metadataFactory,
        Logger logger)
    {
        _albumService = albumService ?? throw new ArgumentNullException(nameof(albumService));
        _trackService = trackService ?? throw new ArgumentNullException(nameof(trackService));
        _artistService = artistService ?? throw new ArgumentNullException(nameof(artistService));
        _historyRecorder = historyRecorder ?? throw new ArgumentNullException(nameof(historyRecorder));
        _notificationFactory = notificationFactory ?? throw new ArgumentNullException(nameof(notificationFactory));
        _metadataFactory = metadataFactory ?? throw new ArgumentNullException(nameof(metadataFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Metadata refresh path — the production churn scenario from #26: switching a metadata
    /// profile to include Singles makes newly fetched singles appear as Added children with
    /// monitored state already assigned, after tracks were inserted by the album refresh and
    /// before any search or RSS pass can act on them.
    /// </summary>
    public void Handle(AlbumInfoRefreshedEvent message)
    {
        var added = message?.Added;
        if (added == null || added.Count == 0 || message!.Artist == null)
        {
            return;
        }

        foreach (var album in added)
        {
            Evaluate(message.Artist, album, "metadata-refresh");
        }
    }

    /// <summary>
    /// Manual album add path. Tracks are usually not yet inserted at this point; evaluation
    /// then fails safe (no catalog tracks → keep monitored) and the album refresh that
    /// follows publishes AlbumUpdatedEvent, which re-evaluates with tracks present.
    /// </summary>
    public void Handle(AlbumAddedEvent message)
    {
        Evaluate(ResolveArtist(message?.Album), message?.Album, "album-added");
    }

    /// <summary>
    /// Album refresh path — covers a manually added single after its track data lands, and
    /// keeps evaluation current when album data is refreshed.
    /// </summary>
    public void Handle(AlbumUpdatedEvent message)
    {
        Evaluate(ResolveArtist(message?.Album), message?.Album, "album-updated");
    }

    /// <summary>
    /// Monitoring-edit path — fires from AlbumService.SetAlbumMonitored (the UI/API album
    /// monitoring toggle) and album edits. Re-evaluation is idempotent: a single the plugin
    /// (or the user) already unmonitored is skipped by the monitored gate. The unmonitor this
    /// service performs publishes AlbumEditedEvent with the persisted unmonitored state, so
    /// the re-entrant dispatch terminates immediately.
    /// </summary>
    public void Handle(AlbumEditedEvent message)
    {
        Evaluate(ResolveArtist(message?.Album), message?.Album, "album-edited");
    }

    public bool TryUnmonitorIfRedundant(Artist artist, Album single, string trigger, SingleCleanupOptions options)
    {
        if (artist == null || single == null || options == null)
        {
            return false;
        }

        if (!options.EagerUnmonitorRedundantSingles)
        {
            _logger.Debug(
                "UniqueSingles eager skip: gate disabled. artistId={0} albumId={1} reason=eager-unmonitor-disabled",
                artist.Id,
                single.Id);
            return false;
        }

        if (!IsSingle(single) || !single.Monitored)
        {
            return false;
        }

        List<Track> singleTracks;
        try
        {
            singleTracks = _trackService.GetTracksByAlbum(single.Id) ?? new List<Track>();
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles eager skip: failed to load single tracks. artistId={0} artist='{1}' singleId={2} single='{3}' reason=track-lookup-failed",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return false;
        }

        if (singleTracks.Count == 0)
        {
            // Common on AlbumAddedEvent before the album refresh inserts track rows.
            _logger.Debug(
                "UniqueSingles eager skip: no catalog tracks yet. artistId={0} artist='{1}' singleId={2} single='{3}' reason=no-catalog-tracks",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return false;
        }

        if (singleTracks.Any(t => t.HasFile))
        {
            // Releases with downloaded content stay owned by the import-time and scheduled
            // cleanup paths, whose unmonitor is followed by file deletion. Unmonitoring here
            // would leak those files, because cleanup skips already-unmonitored releases.
            _logger.Info(
                "UniqueSingles eager skip: single has downloaded content; cleanup path owns it. artistId={0} artist='{1}' singleId={2} single='{3}' reason=has-downloaded-content",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return false;
        }

        var comparisonTracks = GetDownloadedComparisonTracks(artist, single, options);
        var check = TrackMatcher.CheckSingle(singleTracks, comparisonTracks, options.DurationToleranceMs);

        if (!check.IsRedundant)
        {
            // #17 whole-release protection and FlagOnly semantics, unchanged: any catalog
            // track without a Tier-1/Tier-2 match (exclusive content, downloaded or not)
            // keeps the single monitored, and title-only evidence never unmonitors.
            var outcome = check.TrackResults.Any(r => r.Tier == MatchTier.NoMatch) ? "keep-exclusive" : "keep";
            _logger.Info(
                "UniqueSingles eager keep: artistId={0} artist='{1}' singleId={2} single='{3}' outcome={4} reason='{5}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                outcome,
                check.SummaryReason);
            return false;
        }

        try
        {
            _albumService.SetAlbumMonitored(single.Id, false);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles eager unmonitor failure: single stays monitored. artistId={0} artist='{1}' singleId={2} single='{3}' trigger={4} reason=eager-unmonitor-failed",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                trigger);
            return false;
        }

        single.Monitored = false;
        _logger.Info(
            "UniqueSingles eager unmonitor success: single unmonitored before any grab. artistId={0} artist='{1}' singleId={2} single='{3}' trigger={4} reason='{5}'",
            artist.Id,
            artist.Name,
            single.Id,
            single.Title,
            trigger,
            check.SummaryReason);

        // Lidarr History audit row; recorder failures never affect the decision.
        try
        {
            _historyRecorder.RecordUnmonitor(artist, single, $"Eager pre-grab unmonitor (trigger={trigger}): {check.SummaryReason}");
        }
        catch (Exception historyEx)
        {
            _logger.Warn(
                historyEx,
                "UniqueSingles history: action=unmonitor-record-failed artistId={0} albumId={1} reason=history-recorder-threw",
                artist.Id,
                single.Id);
        }

        return true;
    }

    private void Evaluate(Artist? artist, Album? album, string trigger)
    {
        try
        {
            if (artist == null || album == null)
            {
                return;
            }

            // Cheap gates before settings resolution so library-wide album traffic does not
            // pay the factory lookups for releases this path can never act on.
            if (!IsSingle(album) || !album.Monitored)
            {
                return;
            }

            TryUnmonitorIfRedundant(artist, album, trigger, ResolveCleanupOptions());
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles eager evaluation swallowed exception. albumId={0} trigger={1} reason=eager-handler-failed",
                album?.Id,
                trigger);
        }
    }

    /// <summary>
    /// Mirrors SingleCleanupService.GetComparisonTracksByAlbum selection exactly: monitored
    /// releases of the configured comparison types (Album/EP by default), excluding Live,
    /// Remix, DJ-mix and Demo secondary types, contributing only downloaded tracks, and never
    /// the candidate itself (possible when "Single" is a configured comparison type — a
    /// file-less single contributes no downloaded tracks either way).
    /// </summary>
    private List<Track> GetDownloadedComparisonTracks(Artist artist, Album single, SingleCleanupOptions options)
    {
        List<Album> albums;
        try
        {
            albums = _albumService.GetAlbumsByArtist(artist.Id) ?? new List<Album>();
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles eager lookup failure: failed to load artist albums. artistId={0} artist='{1}' reason=album-lookup-failed",
                artist.Id,
                artist.Name);
            return new List<Track>();
        }

        var comparisonTracks = new List<Track>();
        foreach (var album in albums)
        {
            if (album == null || album.Id == single.Id)
            {
                continue;
            }

            if (!options.ShouldCompareAgainstType(album.AlbumType) || !album.Monitored)
            {
                continue;
            }

            if (SingleCleanupService.HasExcludedSecondaryType(album))
            {
                _logger.Debug(
                    "UniqueSingles eager comparison skip: album has an excluded secondary type. albumId={0} album='{1}' secondaryTypes='{2}' reason=excluded-secondary-type",
                    album.Id,
                    album.Title,
                    string.Join(",", album.SecondaryTypes.Select(t => t.Name)));
                continue;
            }

            try
            {
                var albumTracks = _trackService.GetTracksByAlbum(album.Id) ?? new List<Track>();
                comparisonTracks.AddRange(albumTracks.Where(t => t.HasFile));
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "UniqueSingles eager lookup failure: failed to load album tracks. albumId={0} album='{1}' albumType='{2}' reason=track-lookup-failed",
                    album.Id,
                    album.Title,
                    album.AlbumType);
            }
        }

        return comparisonTracks;
    }

    /// <summary>
    /// Settings precedence mirrors UniqueSinglesScanTask.ResolveCleanupOptions: the Connect
    /// (notification) entry is the source of truth, then the Metadata entry, then defaults.
    /// The default snapshot keeps the eager gate on, matching the behavior issue #26 asks for.
    /// </summary>
    private SingleCleanupOptions ResolveCleanupOptions()
    {
        try
        {
            var matching = _notificationFactory.Value.All()
                .Where(d => d.Settings is UniqueSinglesSettings)
                .ToList();

            if (matching.Count > 0)
            {
                if (matching.Count > 1)
                {
                    _logger.Warn(
                        "UniqueSingles eager: multiple UniqueSingles notifications found ({0}), using first",
                        matching.Count);
                }

                return ((UniqueSinglesSettings)matching[0].Settings).ToCleanupOptions();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "UniqueSingles eager: failed to resolve notification settings, falling back");
        }

        try
        {
            var settings = _metadataFactory.Value.All()
                    .Where(d => d.Implementation == nameof(UniqueSinglesScanTask))
                    .Select(d => d.Settings)
                    .OfType<UniqueSinglesSettings>()
                    .FirstOrDefault();

            if (settings != null)
            {
                return settings.ToCleanupOptions();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "UniqueSingles eager: failed to resolve metadata settings, using defaults");
        }

        _logger.Info("UniqueSingles eager: using default settings (no notification or metadata provider settings)");
        return new UniqueSinglesSettings().ToCleanupOptions();
    }

    private Artist? ResolveArtist(Album? album)
    {
        if (album == null)
        {
            return null;
        }

        try
        {
            // Album's parameterless constructor plants a placeholder Artist (Id 0), so a
            // positive id is required before any lookup; ArtistMetadataId is a persisted
            // column and therefore the reliable fallback when the lazy link is absent.
            var artistId = album.ArtistId;
            if (artistId > 0)
            {
                return _artistService.GetArtist(artistId);
            }

            if (album.ArtistMetadataId > 0)
            {
                return _artistService.GetArtistByMetadataId(album.ArtistMetadataId);
            }

            var lazyArtist = album.Artist?.Value;
            return lazyArtist != null && lazyArtist.Id > 0 ? lazyArtist : null;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles eager skip: failed to resolve artist. albumId={0} album='{1}' reason=artist-lookup-failed",
                album.Id,
                album.Title);
            return null;
        }
    }

    private static bool IsSingle(Album album)
    {
        return AlbumTypeComparer.Equals(album.AlbumType, "Single");
    }
}
