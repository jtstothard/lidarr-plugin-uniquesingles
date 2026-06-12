using System.Linq;
using System;
using System.Collections.Generic;

using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Orchestrates safe single cleanup after Lidarr imports. It scopes all work to the imported artist,
/// delegates match decisions to TrackMatcher, unmonitors before deleting, and logs every cleanup decision.
/// </summary>
public interface ISingleCleanupService
{
    void CleanupSinglesForArtist(Artist artist, Album importedAlbum);
    void CleanupSingleSelfCheck(Artist artist, Album importedSingle);
    CleanupResult CleanupSingleSelfCheckWithOptions(Artist artist, Album importedSingle, SingleCleanupOptions options);
    CleanupResult CleanupWithOptions(Artist artist, Album importedAlbum, SingleCleanupOptions options);
    CleanupResult ScanArtistWithOptions(Artist artist, SingleCleanupOptions options);
}

public class SingleCleanupService : ISingleCleanupService
{
    private static readonly StringComparer AlbumTypeComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IAlbumService _albumService;
    private readonly ITrackService _trackService;
    private readonly IMediaFileService _mediaFileService;
    private readonly IDeleteMediaFiles _deleteMediaFiles;
    private readonly Logger _logger;

    public SingleCleanupService(
        IAlbumService albumService,
        ITrackService trackService,
        IMediaFileService mediaFileService,
        IDeleteMediaFiles deleteMediaFiles,
        Logger logger)
    {
        _albumService = albumService ?? throw new ArgumentNullException(nameof(albumService));
        _trackService = trackService ?? throw new ArgumentNullException(nameof(trackService));
        _mediaFileService = mediaFileService ?? throw new ArgumentNullException(nameof(mediaFileService));
        _deleteMediaFiles = deleteMediaFiles ?? throw new ArgumentNullException(nameof(deleteMediaFiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void CleanupSinglesForArtist(Artist artist, Album importedAlbum)
    {
        if (artist == null)
        {
            throw new ArgumentNullException(nameof(artist));
        }

        if (importedAlbum == null)
        {
            throw new ArgumentNullException(nameof(importedAlbum));
        }

        if (!IsAlbumOrEp(importedAlbum))
        {
            _logger.Info(
                "UniqueSingles cleanup skip: imported release is unsupported. artistId={0} artist='{1}' albumId={2} album='{3}' albumType='{4}' reason=unsupported-import-type",
                artist.Id,
                artist.Name,
                importedAlbum.Id,
                importedAlbum.Title,
                importedAlbum.AlbumType);
            return;
        }

        var albums = GetAlbumsForArtist(artist);
        var albumTracks = GetDownloadedAlbumOrEpTracks(albums);
        var candidateSingles = albums
            .Where(a => IsSingle(a) && a.Id != importedAlbum.Id)
            .ToList();

        _logger.Info(
            "UniqueSingles cleanup start: artistId={0} artist='{1}' importedAlbumId={2} importedAlbum='{3}' albumTrackCount={4} candidateSingles={5}",
            artist.Id,
            artist.Name,
            importedAlbum.Id,
            importedAlbum.Title,
            albumTracks.Count,
            candidateSingles.Count);

        foreach (var single in candidateSingles)
        {
            CheckAndCleanSingle(artist, single, importedAlbum, albumTracks);
        }
    }

    public void CleanupSingleSelfCheck(Artist artist, Album importedSingle)
    {
        CleanupSingleSelfCheckWithOptions(artist, importedSingle, CreateLegacyOptions());
    }

    /// <summary>
    /// Self-checks an imported single using configured options and returns statistics.
    /// </summary>
    public CleanupResult CleanupSingleSelfCheckWithOptions(Artist artist, Album importedSingle, SingleCleanupOptions options)
    {
        if (artist == null)
        {
            throw new ArgumentNullException(nameof(artist));
        }

        if (importedSingle == null)
        {
            throw new ArgumentNullException(nameof(importedSingle));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!IsSingle(importedSingle))
        {
            _logger.Info(
                "UniqueSingles self-check skip: imported release is not a single. artistId={0} artist='{1}' albumId={2} album='{3}' albumType='{4}' reason=unsupported-import-type",
                artist.Id,
                artist.Name,
                importedSingle.Id,
                importedSingle.Title,
                importedSingle.AlbumType);
            return new CleanupResult(0, 0, 1, 0, 0, 0);
        }

        var albums = GetAlbumsForArtist(artist);
        var comparisonAlbums = albums.Where(a => a.Id != importedSingle.Id).ToList();
        var albumTracks = GetDownloadedAlbumOrEpTracks(comparisonAlbums, options);

        _logger.Info(
            "UniqueSingles self-check start: artistId={0} artist='{1}' singleId={2} single='{3}' albumTrackCount={4} comparisonReleaseTypes='{5}' durationToleranceMs={6} tier3Action='{7}'",
            artist.Id,
            artist.Name,
            importedSingle.Id,
            importedSingle.Title,
            albumTracks.Count,
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t)),
            options.DurationToleranceMs,
            options.Tier3Action);

        return CheckAndCleanSingleWithStats(artist, importedSingle, null, albumTracks, options);
    }

    /// <summary>
    /// Cleans singles for an artist using configured options and returns statistics.
    /// </summary>
    public CleanupResult CleanupWithOptions(Artist artist, Album importedAlbum, SingleCleanupOptions options)
    {
        if (artist == null)
        {
            throw new ArgumentNullException(nameof(artist));
        }

        if (importedAlbum == null)
        {
            throw new ArgumentNullException(nameof(importedAlbum));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!IsAlbumOrEp(importedAlbum))
        {
            _logger.Info(
                "UniqueSingles cleanup skip: imported release is unsupported. artistId={0} artist='{1}' albumId={2} album='{3}' albumType='{4}' reason=unsupported-import-type",
                artist.Id,
                artist.Name,
                importedAlbum.Id,
                importedAlbum.Title,
                importedAlbum.AlbumType);
            return CleanupResult.Empty;
        }

        var albums = GetAlbumsForArtist(artist);
        var albumTracks = GetDownloadedAlbumOrEpTracks(albums, options);
        var candidateSingles = albums
            .Where(a => IsSingle(a) && a.Id != importedAlbum.Id)
            .ToList();

        _logger.Info(
            "UniqueSingles cleanup start: artistId={0} artist='{1}' importedAlbumId={2} importedAlbum='{3}' albumTrackCount={4} candidateSingles={5} comparisonReleaseTypes='{6}' durationToleranceMs={7} tier3Action='{8}'",
            artist.Id,
            artist.Name,
            importedAlbum.Id,
            importedAlbum.Title,
            albumTracks.Count,
            candidateSingles.Count,
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t)),
            options.DurationToleranceMs,
            options.Tier3Action);

        var result = CleanupResult.Empty;

        foreach (var single in candidateSingles)
        {
            result += CheckAndCleanSingleWithStats(artist, single, importedAlbum, albumTracks, options);
        }

        return result;
    }

    /// <summary>
    /// Scans all singles for an artist using configured options and returns statistics.
    /// </summary>
    public CleanupResult ScanArtistWithOptions(Artist artist, SingleCleanupOptions options)
    {
        if (artist == null)
        {
            throw new ArgumentNullException(nameof(artist));
        }

        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var albums = GetAlbumsForArtist(artist);
        var albumTracks = GetDownloadedAlbumOrEpTracks(albums, options);
        var candidateSingles = albums.Where(IsSingle).ToList();

        _logger.Info(
            "UniqueSingles scan start: artistId={0} artist='{1}' albumTrackCount={2} candidateSingles={3} comparisonReleaseTypes='{4}' durationToleranceMs={5} tier3Action='{6}'",
            artist.Id,
            artist.Name,
            albumTracks.Count,
            candidateSingles.Count,
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t)),
            options.DurationToleranceMs,
            options.Tier3Action);

        var result = CleanupResult.Empty;

        foreach (var single in candidateSingles)
        {
            result += CheckAndCleanSingleWithStats(artist, single, null, albumTracks, options);
        }

        return result;
    }

    private CleanupResult CheckAndCleanSingleWithStats(
        Artist artist,
        Album single,
        Album? comparisonAlbumContext,
        List<Track> albumTracks,
        SingleCleanupOptions options)
    {
        var candidatesChecked = 1;
        var cleaned = 0;
        var skipped = 0;
        var reviewNeeded = 0;
        var unmonitorFailures = 0;
        var deleteFailures = 0;

        if (!single.Monitored)
        {
            _logger.Info(
                "UniqueSingles cleanup skip: single already unmonitored. artistId={0} artist='{1}' singleId={2} single='{3}' reason=already-unmonitored",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
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
                "UniqueSingles cleanup skip: failed to load single tracks. artistId={0} artist='{1}' singleId={2} single='{3}' reason=track-lookup-failed",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
        }

        var downloadedSingleTracks = singleTracks.Where(t => t.HasFile).ToList();
        if (downloadedSingleTracks.Count == 0)
        {
            var tier1Check = TrackMatcher.CheckSingle(singleTracks, albumTracks, options.DurationToleranceMs);
            LogMatchDecision(artist, single, comparisonAlbumContext, tier1Check, options.Tier3Action);

            if (!IsTier1OnlyRedundant(tier1Check))
            {
                _logger.Info(
                    "UniqueSingles cleanup skip: single has no downloaded track files and is not fully Tier-1 redundant. artistId={0} artist='{1}' singleId={2} single='{3}' reason=no-downloaded-single-tracks-no-tier1-match",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title);
                return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
            }

            if (!TryUnmonitorSingle(artist, single, comparisonAlbumContext, tier1Check))
            {
                unmonitorFailures = 1;
                return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, 0);
            }

            cleaned = 1;
            return new CleanupResult(candidatesChecked, cleaned, 0, 0, 0, 0);
        }

        var check = TrackMatcher.CheckSingle(downloadedSingleTracks, albumTracks, options.DurationToleranceMs);
        LogMatchDecision(artist, single, comparisonAlbumContext, check, options.Tier3Action);

        if (!check.IsRedundant)
        {
            // AutoClean: if all tracks matched (none are NoMatch), treat as redundant and proceed to cleanup
            if (options.Tier3Action == Tier3Action.AutoClean && check.TrackResults.All(r => r.Tier != MatchTier.NoMatch))
            {
                _logger.Info(
                    "UniqueSingles auto-clean: Tier-3 match approved for cleanup. artistId={0} artist='{1}' singleId={2} single='{3}' trackTiers='{4}'",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    string.Join(",", check.TrackResults.Select(r => r.Tier)));
                // Fall through to unmonitor+delete path below
            }
            else
            {
                if (options.Tier3Action == Tier3Action.FlagOnly && check.TrackResults.Any(r => r.Tier == MatchTier.Tier3_TitleOnly))
                {
                    reviewNeeded = 1;
                }

                skipped = 1;
                return new CleanupResult(candidatesChecked, cleaned, skipped, reviewNeeded, unmonitorFailures, deleteFailures);
            }
        }

        if (!TryUnmonitorSingle(artist, single, comparisonAlbumContext, check))
        {
            unmonitorFailures = 1;
            return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, 0);
        }

        if (!TryDeleteSingleFiles(artist, single, comparisonAlbumContext))
        {
            cleaned = 1; // Unmonitored successfully
            deleteFailures = 1;
            return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, deleteFailures);
        }

        cleaned = 1;
        return new CleanupResult(candidatesChecked, cleaned, 0, 0, 0, 0);
    }

    private void CheckAndCleanSingle(Artist artist, Album single, Album? comparisonAlbumContext, List<Track> albumTracks)
    {
        if (!single.Monitored)
        {
            _logger.Info(
                "UniqueSingles cleanup skip: single already unmonitored. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=already-unmonitored",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title);
            return;
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
                "UniqueSingles cleanup skip: failed to load single tracks. artistId={0} artist='{1}' singleId={2} single='{3}' reason=track-lookup-failed",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return;
        }

        var downloadedSingleTracks = singleTracks.Where(t => t.HasFile).ToList();
        if (downloadedSingleTracks.Count == 0)
        {
            var tier1Check = TrackMatcher.CheckSingle(singleTracks, albumTracks);
            LogMatchDecision(artist, single, comparisonAlbumContext, tier1Check);

            if (!IsTier1OnlyRedundant(tier1Check))
            {
                _logger.Info(
                    "UniqueSingles cleanup skip: single has no downloaded track files and is not fully Tier-1 redundant. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=no-downloaded-single-tracks-no-tier1-match",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    comparisonAlbumContext?.Id,
                    comparisonAlbumContext?.Title);
                return;
            }

            TryUnmonitorSingle(artist, single, comparisonAlbumContext, tier1Check);
            return;
        }

        var check = TrackMatcher.CheckSingle(downloadedSingleTracks, albumTracks);
        LogMatchDecision(artist, single, comparisonAlbumContext, check);

        if (!check.IsRedundant)
        {
            return;
        }

        if (!TryUnmonitorSingle(artist, single, comparisonAlbumContext, check))
        {
            return;
        }

        TryDeleteSingleFiles(artist, single, comparisonAlbumContext);
    }

    private List<Album> GetAlbumsForArtist(Artist artist)
    {
        try
        {
            return _albumService.GetAlbumsByArtist(artist.Id) ?? new List<Album>();
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles cleanup lookup failure: failed to load artist albums. artistId={0} artist='{1}' reason=album-lookup-failed",
                artist.Id,
                artist.Name);
            return new List<Album>();
        }
    }

    private List<Track> GetDownloadedAlbumOrEpTracks(List<Album> albums, SingleCleanupOptions options)
    {
        var tracks = new List<Track>();

        foreach (var album in albums.Where(a => options.ShouldCompareAgainstType(a.AlbumType)).Where(a => a.Monitored))
        {
            try
            {
                var albumTracks = _trackService.GetTracksByAlbum(album.Id) ?? new List<Track>();
                tracks.AddRange(albumTracks.Where(t => t.HasFile));
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "UniqueSingles cleanup lookup failure: failed to load album tracks. albumId={0} album='{1}' albumType='{2}' reason=track-lookup-failed",
                    album.Id,
                    album.Title,
                    album.AlbumType);
            }
        }

        return tracks;
    }

    private List<Track> GetDownloadedAlbumOrEpTracks(List<Album> albums)
    {
        // Legacy path for S01/S02 callers — uses default Album/EP filtering
        return GetDownloadedAlbumOrEpTracks(albums, CreateLegacyOptions());
    }

    private void LogMatchDecision(Artist artist, Album single, Album? comparisonAlbumContext, SingleRedundancyCheck check, Tier3Action tier3Action = Tier3Action.FlagOnly)
    {
        var decision = check.IsRedundant ? "cleanup-approved" : "cleanup-skipped";

        // AutoClean: Tier-3 matches that will be auto-cleaned should show "cleanup-approved"
        if (!check.IsRedundant && tier3Action == Tier3Action.AutoClean && check.TrackResults.All(r => r.Tier != MatchTier.NoMatch))
        {
            decision = "cleanup-approved";
        }
        foreach (var result in check.TrackResults)
        {
            var isReview = result.Tier == MatchTier.Tier3_TitleOnly && tier3Action == Tier3Action.FlagOnly;
            _logger.Info(
                "UniqueSingles {0}: artistId={1} artist='{2}' singleId={3} single='{4}' comparisonAlbumId={5} comparisonAlbum='{6}' track='{7}' matchedTrack='{8}' matchTier={9} confidence={10} reason='{11}' summary='{12}'",
                isReview ? "review-needed" : decision,
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title,
                result.SingleTrack.Title,
                result.MatchedTrack?.Title,
                result.Tier,
                result.Confidence,
                result.Reason,
                check.SummaryReason);
        }

        if (check.TrackResults.Count == 0)
        {
            _logger.Info(
                "UniqueSingles cleanup-skipped: artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason='{6}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title,
                check.SummaryReason);
        }
    }

    private bool TryUnmonitorSingle(Artist artist, Album single, Album? comparisonAlbumContext, SingleRedundancyCheck check)
    {
        try
        {
            _albumService.SetAlbumMonitored(single.Id, false);
            single.Monitored = false;
            _logger.Info(
                "UniqueSingles unmonitor success: artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason='{6}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title,
                check.SummaryReason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles unmonitor failure: artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=unmonitor-failed-delete-skipped",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title);
            return false;
        }
    }

    private bool TryDeleteSingleFiles(Artist artist, Album single, Album? comparisonAlbumContext)
    {
        List<TrackFile> files;
        try
        {
            files = _mediaFileService.GetFilesByAlbum(single.Id) ?? new List<TrackFile>();
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles delete skip: failed to load media files after unmonitor. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=media-file-lookup-failed",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title);
            return false;
        }

        if (files.Count == 0)
        {
            _logger.Info(
                "UniqueSingles delete skip: no media files to delete after unmonitor. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=no-media-files",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title);
            return true; // Success - nothing to delete
        }

        var allDeleted = true;
        foreach (var file in files)
        {
            try
            {
                _deleteMediaFiles.DeleteTrackFile(artist, file);
                _logger.Info(
                    "UniqueSingles delete success: artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' trackFileId={6} path='{7}'",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    comparisonAlbumContext?.Id,
                    comparisonAlbumContext?.Title,
                    file.Id,
                    file.Path);
            }
            catch (Exception ex)
            {
                allDeleted = false;
                _logger.Warn(
                    ex,
                    "UniqueSingles delete failure: single remains safely unmonitored. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' trackFileId={6} path='{7}' reason=delete-failed",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    comparisonAlbumContext?.Id,
                    comparisonAlbumContext?.Title,
                    file.Id,
                    file.Path);
            }
        }

        return allDeleted;
    }

    private static SingleCleanupOptions CreateLegacyOptions()
    {
        return new SingleCleanupOptions(
            3000,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" },
            Tier3Action.FlagOnly);
    }

    private static bool IsTier1OnlyRedundant(SingleRedundancyCheck check)
    {
        return check.TrackResults.Count > 0 &&
            check.TrackResults.All(result => result.Tier == MatchTier.Tier1_Mbid);
    }

    private static bool IsSingle(Album album)
    {
        return AlbumTypeComparer.Equals(album.AlbumType, "Single");
    }

    private static bool IsAlbumOrEp(Album album)
    {
        return AlbumTypeComparer.Equals(album.AlbumType, "Album") || AlbumTypeComparer.Equals(album.AlbumType, "EP");
    }
}
