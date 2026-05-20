using System.Linq;
using System;
using System.Collections.Generic;
using NzbDrone.Core.MediaFiles;
using NLog;
using NzbDrone.Core.Music;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Plugins;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Executor for full-library redundant single scan.
/// Iterates all monitored artists, finds album/EP releases, and cleans redundant singles.
/// Safe/idempotent: re-running a scan on already-cleaned artists is a no-op.
/// </summary>
public class UniqueSinglesScanCommandService : IExecute<UniqueSinglesScanCommand>
{
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly ITrackService _trackService;
    private readonly IMediaFileService _mediaFileService;
    private readonly IDeleteMediaFiles _deleteMediaFiles;
    private readonly Logger _logger;

    public UniqueSinglesScanCommandService(
        IArtistService artistService,
        IAlbumService albumService,
        ITrackService trackService,
        IMediaFileService mediaFileService,
        IDeleteMediaFiles deleteMediaFiles,
        Logger logger)
    {
        _artistService = artistService ?? throw new ArgumentNullException(nameof(artistService));
        _albumService = albumService ?? throw new ArgumentNullException(nameof(albumService));
        _trackService = trackService ?? throw new ArgumentNullException(nameof(trackService));
        _mediaFileService = mediaFileService ?? throw new ArgumentNullException(nameof(mediaFileService));
        _deleteMediaFiles = deleteMediaFiles ?? throw new ArgumentNullException(nameof(deleteMediaFiles));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Execute(UniqueSinglesScanCommand command)
    {
        if (command == null)
        {
            throw new ArgumentNullException(nameof(command));
        }

        try
        {

            List<Artist> allArtists;
            try
            {
                allArtists = _artistService.GetAllArtists() ?? new List<Artist>();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "UniqueSingles scan failed: could not load artists");
                throw;
            }

            var monitoredArtists = allArtists.Where(a => IsMonitored(a)).ToList();
            var artistsScanned = 0;
            var artistsSkipped = allArtists.Count - monitoredArtists.Count;
            var artistsFailed = 0;

            _logger.Info(
                "UniqueSingles scan: {0} total artists, {1} monitored, {2} skipped (unmonitored)",
                allArtists.Count,
                monitoredArtists.Count,
                artistsSkipped);

            foreach (var artist in monitoredArtists)
            {
                try
                {
                    ScanArtist(artist);
                    artistsScanned++;
                }
                catch (Exception ex)
                {
                    artistsFailed++;
                    _logger.Warn(
                        ex,
                        "UniqueSingles scan: artist scan failed (continuing with next artist). artistId={0} artist='{1}'",
                        artist.Id,
                        artist.Name);
                }
            }

            _logger.Info("UniqueSingles scan complete: artistsScanned={0} artistsSkipped={1} artistsFailed={2}", artistsScanned, artistsSkipped, artistsFailed);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "UniqueSingles scan failed with exception");
            throw;
        }
    }

    private void ScanArtist(Artist artist)
    {
        _logger.Debug("UniqueSingles scan: scanning artist. artistId={0} artist='{1}'", artist.Id, artist.Name);

        var albums = GetAlbumsForArtist(artist);
        if (albums.Count == 0)
        {
            _logger.Debug("UniqueSingles scan: no albums for artist. artistId={0} artist='{1}'", artist.Id, artist.Name);
            return;
        }

        var albumOrEp = albums.FirstOrDefault(IsAlbumOrEp);
        if (albumOrEp == null)
        {
            _logger.Info(
                "UniqueSingles scan: no monitored album/EP found for artist. artistId={0} artist='{1}' reason=no-album-or-ep",
                artist.Id,
                artist.Name);
            return;
        }

        _logger.Info(
            "UniqueSingles scan: found comparison album/EP. artistId={0} artist='{1}' comparisonAlbumId={2} comparisonAlbum='{3}'",
            artist.Id,
            artist.Name,
            albumOrEp.Id,
            albumOrEp.Title);

        var candidateSingles = albums.Where(a => IsSingle(a) && a.Monitored).ToList();
        if (candidateSingles.Count == 0)
        {
            _logger.Debug(
                "UniqueSingles scan: no monitored singles to check. artistId={0} artist='{1}'",
                artist.Id,
                artist.Name);
            return;
        }

        _logger.Info(
            "UniqueSingles scan: checking {0} singles against album/EP. artistId={1} artist='{2}'",
            candidateSingles.Count,
            artist.Id,
            artist.Name);

        var albumTracks = GetDownloadedAlbumOrEpTracks(albums);
        foreach (var single in candidateSingles)
        {
            CheckAndCleanSingle(artist, single, albumOrEp, albumTracks);
        }
    }

    private void CheckAndCleanSingle(Artist artist, Album single, Album comparisonAlbum, List<Track> albumTracks)
    {
        _logger.Debug(
            "UniqueSingles scan: checking single. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4}",
            artist.Id,
            artist.Name,
            single.Id,
            single.Title,
            comparisonAlbum.Id);

        List<Track> singleTracks;
        try
        {
            singleTracks = _trackService.GetTracksByAlbum(single.Id) ?? new List<Track>();
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles scan: failed to load single tracks. artistId={0} artist='{1}' singleId={2} single='{3}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return;
        }

        var downloadedSingleTracks = singleTracks.Where(t => t.HasFile).ToList();
        if (downloadedSingleTracks.Count == 0)
        {
            _logger.Debug(
                "UniqueSingles scan: single has no downloaded tracks. artistId={0} artist='{1}' singleId={2} single='{3}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return;
        }

        var check = TrackMatcher.CheckSingle(downloadedSingleTracks, albumTracks);

        if (!check.IsRedundant)
        {
            _logger.Debug(
                "UniqueSingles scan: single is not redundant. artistId={0} artist='{1}' singleId={2} single='{3}' reason='{4}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                check.SummaryReason);
            return;
        }

        if (!TryUnmonitorSingle(artist, single, comparisonAlbum, check))
        {
            return;
        }

        DeleteSingleFiles(artist, single, comparisonAlbum);
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
                "UniqueSingles scan: failed to load artist albums. artistId={0} artist='{1}'",
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
                    "UniqueSingles scan: failed to load album tracks. albumId={0} album='{1}' albumType='{2}'",
                    album.Id,
                    album.Title,
                    album.AlbumType);
            }
        }

        return tracks;
    }

    private bool TryUnmonitorSingle(Artist artist, Album single, Album comparisonAlbum, SingleRedundancyCheck check)
    {
        try
        {
            _albumService.SetAlbumMonitored(single.Id, false);
            single.Monitored = false;
            _logger.Info(
                "UniqueSingles scan: unmonitored redundant single. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} reason='{5}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbum.Id,
                check.SummaryReason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles scan: failed to unmonitor single. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4}",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                comparisonAlbum.Id);
            return false;
        }
    }

    private void DeleteSingleFiles(Artist artist, Album single, Album comparisonAlbum)
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
                "UniqueSingles scan: failed to load media files after unmonitor. artistId={0} artist='{1}' singleId={2} single='{3}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return;
        }

        if (files.Count == 0)
        {
            _logger.Debug(
                "UniqueSingles scan: no media files to delete. artistId={0} artist='{1}' singleId={2} single='{3}'",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return;
        }

        foreach (var file in files)
        {
            try
            {
                _deleteMediaFiles.DeleteTrackFile(artist, file);
                _logger.Info(
                    "UniqueSingles scan: deleted track file. artistId={0} artist='{1}' singleId={2} single='{3}' trackFileId={4} path='{5}'",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    file.Id,
                    file.Path);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "UniqueSingles scan: failed to delete track file. artistId={0} artist='{1}' singleId={2} single='{3}' trackFileId={4} path='{5}'",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    file.Id,
                    file.Path);
            }
        }
    }

    private static bool IsMonitored(Artist artist)
    {
        // For now, all artists are considered monitored for scanning purposes.
        // Real Lidarr has a Monitored property on Artist.
        return true;
    }

    private static bool IsSingle(Album album)
    {
        return string.Equals(album.AlbumType, "Single", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlbumOrEp(Album album)
    {
        return string.Equals(album.AlbumType, "Album", StringComparison.OrdinalIgnoreCase)
            || string.Equals(album.AlbumType, "EP", StringComparison.OrdinalIgnoreCase);
    }
}
