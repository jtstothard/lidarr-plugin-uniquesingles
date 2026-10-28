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

    private static readonly HashSet<int> ExcludedSecondaryTypeIds = new()
    {
        SecondaryAlbumType.Live.Id,
        SecondaryAlbumType.Remix.Id,
        SecondaryAlbumType.DJMix.Id,
        SecondaryAlbumType.Demo.Id,
    };

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
        var comparisonTracks = GetComparisonTracksByAlbum(comparisonAlbums, options);

        _logger.Info(
            "UniqueSingles self-check start: artistId={0} artist='{1}' singleId={2} single='{3}' albumTrackCount={4} comparisonReleaseTypes='{5}' durationToleranceMs={6} tier3Action='{7}'",
            artist.Id,
            artist.Name,
            importedSingle.Id,
            importedSingle.Title,
            comparisonTracks.Values.Sum(t => t.Count),
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t)),
            options.DurationToleranceMs,
            options.Tier3Action);

        return CheckAndCleanSingleWithStats(artist, importedSingle, null, comparisonTracks, options);
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
        var comparisonTracks = GetComparisonTracksByAlbum(albums, options);
        var candidateSingles = albums
            .Where(a => IsSingle(a) && a.Id != importedAlbum.Id)
            .ToList();

        _logger.Info(
            "UniqueSingles cleanup start: artistId={0} artist='{1}' importedAlbumId={2} importedAlbum='{3}' albumTrackCount={4} candidateSingles={5} comparisonReleaseTypes='{6}' durationToleranceMs={7} tier3Action='{8}'",
            artist.Id,
            artist.Name,
            importedAlbum.Id,
            importedAlbum.Title,
            comparisonTracks.Values.Sum(t => t.Count),
            candidateSingles.Count,
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t)),
            options.DurationToleranceMs,
            options.Tier3Action);

        var result = CleanupResult.Empty;

        foreach (var single in candidateSingles)
        {
            result += CheckAndCleanSingleWithStats(artist, single, importedAlbum, comparisonTracks, options);
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
        var comparisonTracks = GetComparisonTracksByAlbum(albums, options);
        var candidateSingles = albums.Where(IsSingle).ToList();

        _logger.Info(
            "UniqueSingles scan start: artistId={0} artist='{1}' albumTrackCount={2} candidateSingles={3} comparisonReleaseTypes='{4}' durationToleranceMs={5} tier3Action='{6}'",
            artist.Id,
            artist.Name,
            comparisonTracks.Values.Sum(t => t.Count),
            candidateSingles.Count,
            string.Join(",", options.ComparisonReleaseTypes.OrderBy(t => t)),
            options.DurationToleranceMs,
            options.Tier3Action);

        var result = CleanupResult.Empty;

        foreach (var single in candidateSingles)
        {
            result += CheckAndCleanSingleWithStats(artist, single, null, comparisonTracks, options);
        }

        return result;
    }

    private CleanupResult CheckAndCleanSingleWithStats(
        Artist artist,
        Album single,
        Album? comparisonAlbumContext,
        Dictionary<int, List<Track>> comparisonTracks,
        SingleCleanupOptions options)
    {
        // Never compare a single against its own tracks (possible when "Single" is a comparison type).
        var albumTracks = comparisonTracks
            .Where(entry => entry.Key != single.Id)
            .SelectMany(entry => entry.Value)
            .ToList();

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
            // Check if single is fully Tier-1 redundant (exact MBID matches) before skipping
            bool isTier1Redundant = TrackMatcher.IsTier1OnlyRedundant(singleTracks, albumTracks);

            if (isTier1Redundant)
            {
                _logger.Info(
                    "UniqueSingles Tier-1 unmonitor: single has no downloaded files but is fully Tier-1 redundant (exact MBID matches). artistId={0} artist='{1}' singleId={2} single='{3}' reason=tier1-mbid-matches-no-files",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title);

                // Unmonitor but don't delete (no files to delete)
                if (!TryUnmonitorSingle(artist, single, comparisonAlbumContext,
                    new SingleRedundancyCheck(true, new List<MatchResult>(),
                    "Fully Tier-1 redundant by MBID matching (no files present).")))
                {
                    unmonitorFailures = 1;
                    return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, 0);
                }

                // Successfully unmonitored, no deletion needed
                cleaned = 1;
                return new CleanupResult(candidatesChecked, cleaned, 0, 0, 0, 0);
            }

            _logger.Info(
                "UniqueSingles cleanup skip: single has no downloaded track files and is not fully Tier-1 redundant. artistId={0} artist='{1}' singleId={2} single='{3}' reason=no-downloaded-single-tracks-not-tier1",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title);
            return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
        }

        ExactAudioReleaseReport? cleanupReport = null;
        var check = TrackMatcher.CheckSingle(downloadedSingleTracks, albumTracks, options.DurationToleranceMs);
        if (!check.IsRedundant && options.ExactAudioAnalysis)
        {
            var report = new ExactAudioReleaseReporter(_mediaFileService, _logger, new FfmpegExactAudioVerifier(options.ExactAudioDecoderPath))
                .Analyze(single, singleTracks, comparisonTracks, options);
            if (report.Complete)
            {
                if (!options.ExactAudioCleanup)
                    return new CleanupResult(1, 0, 1, 0, 0, 0, exactAudioReports: 1);
                cleanupReport = report;
                check = new SingleRedundancyCheck(true, check.TrackResults, "Complete verified exact-audio coverage; explicit cleanup opt-in.");
            }
        }

        LogMatchDecision(artist, single, comparisonAlbumContext, check, options.Tier3Action, comparisonTracks);

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
                if (NeedsReview(check, options.Tier3Action))
                {
                    reviewNeeded = 1;
                }

                skipped = 1;
                return new CleanupResult(candidatesChecked, cleaned, skipped, reviewNeeded, unmonitorFailures, deleteFailures);
            }
        }

        if (cleanupReport != null && !RevalidateExactCleanup(artist, single, cleanupReport, options))
            return new CleanupResult(1, 0, 1, 0, 0, 0);

        if (!TryUnmonitorSingle(artist, single, comparisonAlbumContext, check))
        {
            unmonitorFailures = 1;
            return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, 0);
        }

        // The single is now unmonitored and its files are about to be deleted, so it can no longer
        // stand in for another candidate. Without this, two identical singles would each match the
        // other and both be deleted.
        comparisonTracks.Remove(single.Id);

        if (!TryDeleteSingleFiles(artist, single, comparisonAlbumContext,
            cleanupReport == null ? null : file => RevalidateExactCleanup(artist, single, cleanupReport, options, file)))
        {
            cleaned = 1; // Unmonitored successfully
            deleteFailures = 1;
            return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, deleteFailures);
        }

        cleaned = 1;
        return new CleanupResult(candidatesChecked, cleaned, 0, 0, 0, 0);
    }

    // Re-read repository identities and hash actual bytes. Snapshot evidence (including cached
    // evidence) is never authority on its own. Repeated immediately before each destructive call.
    private bool RevalidateExactCleanup(Artist artist, Album single, ExactAudioReleaseReport report,
        SingleCleanupOptions options, TrackFile? deleting = null)
    {
        try
        {
            if (options.AudioCancellation.IsCancellationRequested) throw new InvalidOperationException("cancelled");
            var albums = _albumService.GetAlbumsByArtist(artist.Id);
            var sources = GetComparisonTracksByAlbum(albums, options);
            var required = _trackService.GetTracksByAlbum(single.Id);
            var verified = report.Tracks.Where(t => t.Evidence.Status == AudioEvidenceStatus.VerifiedExact).ToList();
            if (!report.Complete || required == null || required.Count == 0 || required.Count != verified.Count)
                throw new InvalidOperationException("coverage-changed");
            var candidateFiles = _mediaFileService.GetFilesByAlbum(single.Id);
            // No extra unverified files may be swept up by whole-release deletion.
            if (candidateFiles.Any(f => !required.Any(t => t.TrackFileId == f.Id)))
                throw new InvalidOperationException("unverified-extra-file");
            foreach (var track in required)
            {
                var evidence = verified.Single(e => e.CandidateTrackId == track.Id);
                if (evidence.SourceReleaseId == single.Id || !sources.TryGetValue(evidence.SourceReleaseId!.Value, out var sourceTracks))
                    throw new InvalidOperationException("source-ineligible");
                var source = sourceTracks.Single(t => t.Id == evidence.SourceTrackId);
                var sourceFile = _mediaFileService.GetFilesByAlbum(evidence.SourceReleaseId.Value).Single(f => f.Id == source.TrackFileId);
                if (candidateFiles.Any(f => string.Equals(System.IO.Path.GetFullPath(f.Path), System.IO.Path.GetFullPath(sourceFile.Path), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("source-shares-candidate-path");
                if (sourceFile.Path != evidence.SourcePath || !ContentMatches(sourceFile.Path, evidence.Evidence.Source?.FileDigest))
                    throw new InvalidOperationException("source-content-changed");
                if (deleting == null || track.TrackFileId == deleting.Id)
                {
                    var candidateFile = candidateFiles.Single(f => f.Id == track.TrackFileId);
                    if (!track.HasFile || candidateFile.Path != evidence.CandidatePath
                        || (deleting != null && deleting.Path != candidateFile.Path)
                        || !ContentMatches(candidateFile.Path, evidence.Evidence.Candidate?.FileDigest))
                        throw new InvalidOperationException("candidate-content-changed");
                }
            }
            if (deleting != null && !required.Any(t => t.TrackFileId == deleting.Id))
                throw new InvalidOperationException("unverified-delete-file");
            _logger.Info("UniqueSingles exact-audio cleanup: candidateReleaseId={0} decision=revalidated action={1}", single.Id, deleting == null ? "unmonitor" : "delete");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn("UniqueSingles exact-audio cleanup: candidateReleaseId={0} decision=keep reason=revalidation-failed detail={1}", single.Id, ex.Message);
            return false;
        }
    }

    private static bool ContentMatches(string path, string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        using var stream = System.IO.File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) return false;
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), digest, StringComparison.OrdinalIgnoreCase);
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
            // Check if single is fully Tier-1 redundant (exact MBID matches) before skipping
            bool isTier1Redundant = TrackMatcher.IsTier1OnlyRedundant(singleTracks, albumTracks);

            if (isTier1Redundant)
            {
                _logger.Info(
                    "UniqueSingles Tier-1 unmonitor: single has no downloaded files but is fully Tier-1 redundant (exact MBID matches). artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=tier1-mbid-matches-no-files",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title,
                    comparisonAlbumContext?.Id,
                    comparisonAlbumContext?.Title);

                // Unmonitor but don't delete (no files to delete)
                TryUnmonitorSingle(artist, single, comparisonAlbumContext,
                    new SingleRedundancyCheck(true, new List<MatchResult>(),
                    "Fully Tier-1 redundant by MBID matching (no files present)."));
                return;
            }

            _logger.Info(
                "UniqueSingles cleanup skip: single has no downloaded track files and is not fully Tier-1 redundant. artistId={0} artist='{1}' singleId={2} single='{3}' comparisonAlbumId={4} comparisonAlbum='{5}' reason=no-downloaded-single-tracks-not-tier1",
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
        return GetComparisonTracksByAlbum(albums, options).Values.SelectMany(t => t).ToList();
    }

    /// <summary>
    /// Loads downloaded comparison tracks grouped by source album id, so a candidate single's
    /// own tracks can be excluded when "Single" is a configured comparison type.
    /// </summary>
    private Dictionary<int, List<Track>> GetComparisonTracksByAlbum(List<Album> albums, SingleCleanupOptions options)
    {
        var tracks = new Dictionary<int, List<Track>>();

        foreach (var album in albums.Where(a => options.ShouldCompareAgainstType(a.AlbumType)).Where(a => a.Monitored))
        {
            if (HasExcludedSecondaryType(album))
            {
                _logger.Debug(
                    "UniqueSingles comparison skip: album has an excluded secondary type. albumId={0} album='{1}' secondaryTypes='{2}' reason=excluded-secondary-type",
                    album.Id,
                    album.Title,
                    string.Join(",", album.SecondaryTypes.Select(t => t.Name)));
                continue;
            }

            try
            {
                var albumTracks = _trackService.GetTracksByAlbum(album.Id) ?? new List<Track>();
                tracks[album.Id] = albumTracks.Where(t => t.HasFile).ToList();
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

    private static bool NeedsReview(SingleRedundancyCheck check, Tier3Action action)
    {
        return action == Tier3Action.FlagOnly
            && !check.TrackResults.Any(r => r.Tier == MatchTier.NoMatch)
            && check.TrackResults.Any(r => r.Tier == MatchTier.Tier3_TitleOnly);
    }

    private void LogMatchDecision(Artist artist, Album single, Album? comparisonAlbumContext, SingleRedundancyCheck check, Tier3Action tier3Action = Tier3Action.FlagOnly, Dictionary<int, List<Track>>? comparisonTracks = null)
    {
        var decision = check.IsRedundant ? "cleanup-approved" : "cleanup-skipped";

        // AutoClean: Tier-3 matches that will be auto-cleaned should show "cleanup-approved"
        if (!check.IsRedundant && tier3Action == Tier3Action.AutoClean && check.TrackResults.All(r => r.Tier != MatchTier.NoMatch))
        {
            decision = "cleanup-approved";
        }

        if (!check.IsRedundant && check.TrackResults.Any(r => r.Tier == MatchTier.NoMatch))
        {
            decision = "keep-exclusive";
        }
        else if (!check.IsRedundant && NeedsReview(check, tier3Action))
        {
            decision = "review-needed";
        }

        var reason = check.SummaryReason;
        if (!check.IsRedundant && decision == "cleanup-approved")
        {
            reason = "All tracks have eligible matches; configured Tier 3 AutoClean permits title-only cleanup.";
        }
        else if (decision == "cleanup-skipped" && tier3Action == Tier3Action.Skip)
        {
            reason = "Title-only evidence is unresolved; configured Tier 3 Skip keeps this release without a review flag.";
        }

        _logger.Info(
            "UniqueSingles {0}: artistId={1} artist='{2}' singleId={3} single='{4}' reason='{5}' outcome={6}",
            decision, artist.Id, artist.Name, single.Id, single.Title, reason,
            decision == "review-needed" ? "unresolved-review" : decision);

        foreach (var result in check.TrackResults)
        {
            // Use the repository grouping: Track.AlbumId may be unset on materialized tracks.
            var matchedAlbumId = result.MatchedTrack == null ? (int?)null
                : comparisonTracks?.Where(entry => entry.Value.Contains(result.MatchedTrack))
                    .Select(entry => (int?)entry.Key).FirstOrDefault() ?? result.MatchedTrack.AlbumId;
            _logger.Info(
                "UniqueSingles {0}: artistId={1} artist='{2}' singleId={3} single='{4}' comparisonAlbumId={5} comparisonAlbum='{6}' track='{7}' matchedTrack='{8}' matchTier={9} confidence={10} reason='{11}' summary='{12}' trackId={13} matchedAlbumId={14} matchedTrackId={15}",
                "track-evidence",
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
                check.SummaryReason,
                result.SingleTrack.Id,
                matchedAlbumId,
                result.MatchedTrack?.Id);
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

    private bool TryDeleteSingleFiles(Artist artist, Album single, Album? comparisonAlbumContext, Func<TrackFile, bool>? validate = null)
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
                if (validate != null && !validate(file)) return false;
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

    /// <summary>
    /// Releases whose tracks are usually different recordings that share the studio title
    /// (live, remix, DJ-mix, demo). A title + duration match against them is not evidence
    /// that the single is redundant, so they are never used as comparison sources.
    /// Lidarr stores these as secondary types; the primary type is still "Album" or "EP".
    /// </summary>
    private static bool HasExcludedSecondaryType(Album album)
    {
        return album.SecondaryTypes != null && album.SecondaryTypes.Any(t => ExcludedSecondaryTypeIds.Contains(t.Id));
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
