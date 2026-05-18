using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using UniqueSingles.Matching;

namespace UniqueSingles;

/// <summary>
/// Orchestrates safe single cleanup after Lidarr imports. It scopes all work to the imported artist,
/// delegates match decisions to TrackMatcher, unmonitors before deleting, and logs every cleanup decision.
/// </summary>
public interface ISingleCleanupService
{
    void CleanupSinglesForArtist(Artist artist, Album importedAlbum);
    void CleanupSingleSelfCheck(Artist artist, Album importedSingle);
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
        if (artist == null)
        {
            throw new ArgumentNullException(nameof(artist));
        }

        if (importedSingle == null)
        {
            throw new ArgumentNullException(nameof(importedSingle));
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
            return;
        }

        var albums = GetAlbumsForArtist(artist);
        var albumTracks = GetDownloadedAlbumOrEpTracks(albums);

        _logger.Info(
            "UniqueSingles self-check start: artistId={0} artist='{1}' singleId={2} single='{3}' albumTrackCount={4}",
            artist.Id,
            artist.Name,
            importedSingle.Id,
            importedSingle.Title,
            albumTracks.Count);

        CheckAndCleanSingle(artist, importedSingle, null, albumTracks);
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
            _logger.Info(
                "UniqueSingles cleanup skip: single has no downloaded track files. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=no-downloaded-single-tracks",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbumContext?.Id,
                comparisonAlbumContext?.Title);
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

        DeleteSingleFiles(artist, single, comparisonAlbumContext);
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

    private List<Track> GetDownloadedAlbumOrEpTracks(List<Album> albums)
    {
        var tracks = new List<Track>();

        foreach (var album in albums.Where(IsAlbumOrEp).Where(a => a.Monitored))
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

    private void LogMatchDecision(Artist artist, Album single, Album? comparisonAlbumContext, SingleRedundancyCheck check)
    {
        var decision = check.IsRedundant ? "cleanup-approved" : "cleanup-skipped";
        foreach (var result in check.TrackResults)
        {
            var isReview = result.Tier == MatchTier.Tier3_TitleOnly;
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

    private void DeleteSingleFiles(Artist artist, Album single, Album? comparisonAlbumContext)
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
            return;
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
            return;
        }

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
