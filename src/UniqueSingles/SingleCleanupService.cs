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

    /// <summary>
    /// Releases whose tracks are usually different recordings that share the studio title
    /// (live, remix, DJ-mix, demo). A title + duration match against them is not evidence
    /// that the single is redundant, so they are never used as comparison sources.
    /// Lidarr stores these as secondary types; the primary type is still "Album" or "EP".
    /// Internal so the eager pre-grab path (EagerSingleUnmonitorService) applies the same rule.
    /// </summary>
    internal static readonly HashSet<int> ExcludedSecondaryTypeIds = new()
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
    private readonly ICleanupHistoryRecorder _historyRecorder;
    private readonly IInFlightReleaseProbe _inFlightProbe;
    private readonly ISingleFolderCleanup _folderCleanup;
    private readonly Logger _logger;

    public SingleCleanupService(
        IAlbumService albumService,
        ITrackService trackService,
        IMediaFileService mediaFileService,
        IDeleteMediaFiles deleteMediaFiles,
        ICleanupHistoryRecorder historyRecorder,
        IInFlightReleaseProbe inFlightProbe,
        ISingleFolderCleanup folderCleanup,
        Logger logger)
    {
        _albumService = albumService ?? throw new ArgumentNullException(nameof(albumService));
        _trackService = trackService ?? throw new ArgumentNullException(nameof(trackService));
        _mediaFileService = mediaFileService ?? throw new ArgumentNullException(nameof(mediaFileService));
        _deleteMediaFiles = deleteMediaFiles ?? throw new ArgumentNullException(nameof(deleteMediaFiles));
        _historyRecorder = historyRecorder ?? throw new ArgumentNullException(nameof(historyRecorder));
        _inFlightProbe = inFlightProbe ?? throw new ArgumentNullException(nameof(inFlightProbe));
        _folderCleanup = folderCleanup ?? throw new ArgumentNullException(nameof(folderCleanup));
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
        var comparisonTracks = GetComparisonTracksByAlbum(albums, CreateLegacyOptions());
        var candidateSingles = albums
            .Where(a => IsSingle(a) && a.Id != importedAlbum.Id)
            .ToList();

        _logger.Info(
            "UniqueSingles cleanup start: artistId={0} artist='{1}' importedAlbumId={2} importedAlbum='{3}' albumTrackCount={4} candidateSingles={5}",
            artist.Id,
            artist.Name,
            importedAlbum.Id,
            importedAlbum.Title,
            comparisonTracks.Values.Sum(t => t.Count),
            candidateSingles.Count);

        foreach (var single in candidateSingles)
        {
            CheckAndCleanSingle(artist, single, importedAlbum, comparisonTracks);
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
        var candidateSingles = OrderCandidatesForReviewPriority(
            albums.Where(a => IsSingle(a) && a.Id != importedAlbum.Id).ToList(),
            comparisonTracks,
            options);

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
        var candidateSingles = OrderCandidatesForReviewPriority(albums.Where(IsSingle).ToList(), comparisonTracks, options);

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

        // Folders stranded by earlier versions, which deleted a single's audio but left its
        // sidecars. Failures never affect the scan result.
        try
        {
            _folderCleanup.SweepStrandedFolders(artist, albums);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles folder sweep failure: artistId={0} artist='{1}' reason=folder-sweep-failed",
                artist.Id,
                artist.Name);
        }

        return result;
    }

    private CleanupResult CheckAndCleanSingleWithStats(
        Artist artist,
        Album single,
        Album? comparisonAlbumContext,
        ComparisonTrackSet comparisonTracks,
        SingleCleanupOptions options)
    {
        // Never compare a single against its own tracks (possible when "Single" is a comparison type).
        var albumTracks = comparisonTracks.StandardTracks(single.Id);
        var tier1OnlyTracks = comparisonTracks.Tier1OnlyTracks(single.Id);

        var candidatesChecked = 1;
        var cleaned = 0;
        var skipped = 0;
        var reviewNeeded = 0;
        var unmonitorFailures = 0;
        var deleteFailures = 0;

        // Leak backstop: a release the plugin unmonitored as redundant can still receive files
        // (a grab already in flight, or a Lidarr search that ignores the monitored flag). Those
        // files are evaluated under the normal cleanup rules instead of being skipped. Releases
        // unmonitored by the user or by Lidarr (no plugin History row) stay untouched.
        if (IsCandidateProtectedBySecondaryType(artist, single, comparisonAlbumContext))
        {
            return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
        }

        var pluginUnmonitored = false;
        if (!single.Monitored)
        {
            if (_historyRecorder.GetProvenance(single.Id) != UnmonitorProvenance.PluginUnmonitored)
            {
                _logger.Info(
                    "UniqueSingles cleanup skip: single already unmonitored. artistId={0} artist='{1}' singleId={2} single='{3}' reason=already-unmonitored",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title);
                return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
            }

            pluginUnmonitored = true;
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
        if (pluginUnmonitored)
        {
            if (downloadedSingleTracks.Count == 0)
            {
                _logger.Info(
                    "UniqueSingles cleanup skip: single already unmonitored. artistId={0} artist='{1}' singleId={2} single='{3}' reason=already-unmonitored",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title);
                return new CleanupResult(candidatesChecked, cleaned, 1, reviewNeeded, unmonitorFailures, deleteFailures);
            }

            _logger.Info(
                "UniqueSingles leak backstop: release the plugin unmonitored as redundant now has downloaded files; evaluating under normal cleanup rules. artistId={0} artist='{1}' singleId={2} single='{3}' downloadedTracks={4} reason=plugin-unmonitored-has-files",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                downloadedSingleTracks.Count);
        }

        if (downloadedSingleTracks.Count == 0)
        {
            // Check if single is fully Tier-1 redundant (exact MBID matches) before skipping
            bool isTier1Redundant = TrackMatcher.IsTier1OnlyRedundant(singleTracks, albumTracks.Concat(tier1OnlyTracks).ToList());

            if (isTier1Redundant)
            {
                _logger.Info(
                    "UniqueSingles Tier-1 unmonitor: single has no downloaded files but is fully Tier-1 redundant (exact MBID matches). artistId={0} artist='{1}' singleId={2} single='{3}' reason=tier1-mbid-matches-no-files",
                    artist.Id,
                    artist.Name,
                    single.Id,
                    single.Title);

                if (!MayUnmonitorFilelessSingle(artist, single))
                {
                    return new CleanupResult(candidatesChecked, cleaned, 1, 0, 0, 0);
                }

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

            // Same rule as the eager path (#26), applied by the scheduled scan and import-time
            // cleanup too: a file-less single whose every catalog track has a Tier-1/Tier-2 match
            // against downloaded comparison tracks is unmonitored (never deleted; there are no
            // files). Without this, singles that existed before their album was downloaded were
            // only ever re-checked for exact MBID matches and stayed wanted forever. #17
            // whole-release protection and Tier-3 semantics are those of CheckSingle: any
            // unmatched or title-only track keeps the single monitored.
            if (options.EagerUnmonitorRedundantSingles)
            {
                var filelessCheck = TrackMatcher.CheckSingle(singleTracks, albumTracks, tier1OnlyTracks, options.DurationToleranceMs);
                if (filelessCheck.IsRedundant)
                {
                    _logger.Info(
                        "UniqueSingles Tier-2 unmonitor: single has no downloaded files and every catalog track is Tier-1/Tier-2 redundant against downloaded comparison tracks. artistId={0} artist='{1}' singleId={2} single='{3}' reason=tier2-matches-no-files",
                        artist.Id,
                        artist.Name,
                        single.Id,
                        single.Title);

                    if (!MayUnmonitorFilelessSingle(artist, single))
                    {
                        return new CleanupResult(candidatesChecked, cleaned, 1, 0, 0, 0);
                    }

                    if (!TryUnmonitorSingle(artist, single, comparisonAlbumContext, filelessCheck))
                    {
                        unmonitorFailures = 1;
                        return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, 0);
                    }

                    cleaned = 1;
                    return new CleanupResult(candidatesChecked, cleaned, 0, 0, 0, 0);
                }
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
        // Whole-release protection (#17): every catalog track protects the release, including
        // undownloaded ones. A track without a Tier-1/Tier-2 redundant match is exclusive
        // content no matter whether it has a file yet, matching the strict all-catalog-tracks
        // rule the exact-audio path already applies. Metadata evidence alone therefore never
        // cleans a release whose catalog contains unmatched exclusive tracks.
        var check = TrackMatcher.CheckSingle(singleTracks, albumTracks, tier1OnlyTracks, options.DurationToleranceMs);
        if (!check.IsRedundant && options.ExactAudioAnalysis)
        {
            // Review-needed candidates draw on the budget's reserved review lane and are ordered
            // first, so comparison attempts are not consumed by earlier non-review candidates.
            var reviewPriority = NeedsReview(check, options.Tier3Action);
            var report = new ExactAudioReleaseReporter(_mediaFileService, _logger, new FfmpegExactAudioVerifier(options.ExactAudioDecoderPath))
                .Analyze(single, singleTracks, comparisonTracks, options, reviewPriority);
            if (report.Complete)
            {
                if (options.ExactAudioCleanup)
                {
                    cleanupReport = report;
                    check = new SingleRedundancyCheck(true, check.TrackResults, "Complete verified exact-audio coverage; explicit cleanup opt-in.");
                }
                else if (options.Tier3Action != Tier3Action.AutoClean || check.TrackResults.Any(r => r.Tier == MatchTier.NoMatch))
                {
                    return new CleanupResult(1, 0, 1, 0, 0, 0, exactAudioReports: 1);
                }
                // Report-only evidence grants no authority. Preserve the existing metadata
                // AutoClean decision below when its own match requirements are satisfied.
            }
            else
            {
                // Audio coverage includes every catalog track, including undownloaded
                // exclusive B-sides. Give that whole-release keep decision precedence
                // over review of the downloaded subset. This grants no cleanup authority.
                check = TrackMatcher.CheckSingle(singleTracks, albumTracks, tier1OnlyTracks, options.DurationToleranceMs);
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
                // Exclusive-content keeps get their own aggregate so summary counters reconcile
                // with the per-release outcome=keep-exclusive lines; they are no longer folded
                // into skipped. Review-needed outcomes remain skipped + reviewNeeded.
                if (check.TrackResults.Any(r => r.Tier == MatchTier.NoMatch))
                {
                    return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, deleteFailures, keepExclusive: 1);
                }

                if (NeedsReview(check, options.Tier3Action))
                {
                    reviewNeeded = 1;
                }

                skipped = 1;
                return new CleanupResult(candidatesChecked, cleaned, skipped, reviewNeeded, unmonitorFailures, deleteFailures);
            }
        }

        if (!pluginUnmonitored && IsUserRemonitored(artist, single))
        {
            return new CleanupResult(candidatesChecked, cleaned, 1, 0, 0, 0);
        }

        if (cleanupReport != null && !RevalidateExactCleanup(artist, single, cleanupReport, options, null, null, out _))
            return new CleanupResult(1, 0, 1, 0, 0, 0);

        // A plugin-unmonitored release is already unmonitored and already has its audit row.
        if (!pluginUnmonitored && !TryUnmonitorSingle(artist, single, comparisonAlbumContext, check))
        {
            unmonitorFailures = 1;
            return new CleanupResult(candidatesChecked, cleaned, 0, 0, unmonitorFailures, 0);
        }

        // The single is now unmonitored and its files are about to be deleted, so it can no longer
        // stand in for another candidate. Without this, two identical singles would each match the
        // other and both be deleted.
        comparisonTracks.Remove(single.Id);

        if (!TryDeleteSingleFiles(artist, single, comparisonAlbumContext,
            cleanupReport == null ? null : (file, stream) =>
                RevalidateExactCleanup(artist, single, cleanupReport, options, file, stream, out var verifiedDigest)
                    ? verifiedDigest : null,
            cleanupReport == null ? null : file => VerifyRetainedSourcesAfterDelete(single, cleanupReport, file)))
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
    // At deletion time the deleting file's content is verified through the caller's open guard
    // handle so the identity check and the destructive call anchor on the same bytes.
    private bool RevalidateExactCleanup(Artist artist, Album single, ExactAudioReleaseReport report,
        SingleCleanupOptions options, TrackFile? deleting, System.IO.FileStream? deletingStream,
        out string? verifiedDigest)
    {
        verifiedDigest = null;
        try
        {
            if (options.AudioCancellation.IsCancellationRequested) throw new InvalidOperationException("cancelled");
            // Deleting files must be verified through a held delete-guard handle, never by a
            // fresh path read that external mutation could invalidate before the delete call.
            if (deleting != null && deletingStream == null) throw new InvalidOperationException("delete-guard-missing");
            var albums = _albumService.GetAlbumsByArtist(artist.Id);
            // The candidate's own secondary types are re-read too: stale or changed metadata
            // that now marks it a distinct version (Live/Remix/DJ-mix/Demo, or unreadable)
            // withdraws the authority granted at decision time.
            var freshSingle = albums.FirstOrDefault(a => a.Id == single.Id) ?? single;
            if (IsProtectedCandidate(freshSingle) || IsProtectedCandidate(single))
                throw new InvalidOperationException("candidate-secondary-type-protected");
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
                // Compare real paths: a symlink (to the file, or through a symlinked directory)
                // makes a "survivor" that is only another name for the candidate's inode, so
                // deleting the candidate path would leave zero surviving copies. Hardlinks are
                // distinct directory entries for one inode, so their data survives and they
                // compare as different paths.
                var sourceRealPath = ResolveRealPath(sourceFile.Path);
                if (candidateFiles.Any(f => string.Equals(ResolveRealPath(f.Path), sourceRealPath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("source-shares-candidate-path");
                if (sourceFile.Path != evidence.SourcePath || !ContentMatches(sourceFile.Path, evidence.Evidence.Source?.FileDigest))
                    throw new InvalidOperationException("source-content-changed");
                if (deleting == null || track.TrackFileId == deleting.Id)
                {
                    var candidateFile = candidateFiles.Single(f => f.Id == track.TrackFileId);
                    var digest = evidence.Evidence.Candidate?.FileDigest;
                    var candidateUnchanged = deletingStream != null
                        ? ContentMatches(deletingStream, digest)
                        : ContentMatches(candidateFile.Path, digest);
                    if (!track.HasFile || candidateFile.Path != evidence.CandidatePath
                        || (deleting != null && deleting.Path != candidateFile.Path)
                        || !candidateUnchanged)
                        throw new InvalidOperationException("candidate-content-changed");
                    if (deleting != null) verifiedDigest = digest;
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

    /// <summary>
    /// Post-delete survivor check for exact-audio cleanup. For every verified track stored in
    /// the file that was just deleted, the retained source named in the evidence must still exist
    /// at its path with the digest verified before deletion. Returns a failure reason token, or
    /// null when every survivor is intact. Works from the report's snapshot, never from
    /// repository state that Lidarr may already have updated for the deletion.
    /// </summary>
    private string? VerifyRetainedSourcesAfterDelete(Album single, ExactAudioReleaseReport report, TrackFile deleted)
    {
        var evidences = report.Tracks
            .Where(t => t.Evidence.Status == AudioEvidenceStatus.VerifiedExact && t.CandidatePath == deleted.Path)
            .ToList();
        if (evidences.Count == 0)
        {
            return "source-evidence-missing-post-delete";
        }

        foreach (var evidence in evidences)
        {
            var sourcePath = evidence.SourcePath;
            var digest = evidence.Evidence.Source?.FileDigest;
            if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(digest))
            {
                return "source-evidence-missing-post-delete";
            }

            try
            {
                if (!System.IO.File.Exists(sourcePath))
                {
                    return "source-vanished-post-delete";
                }

                if (!ContentMatches(sourcePath, digest))
                {
                    return "source-content-changed-post-delete";
                }
            }
            catch (Exception ex) when (ex is System.IO.FileNotFoundException || ex is System.IO.DirectoryNotFoundException)
            {
                return "source-vanished-post-delete";
            }
            catch (Exception)
            {
                return "source-verify-failed-post-delete";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a path to its real location, following symlinks in every component (the file
    /// itself and any parent directory). Used for the survivor-alias check, where a symlinked
    /// "source" would otherwise look like a second copy of the file about to be deleted.
    /// Throws when the path cannot be resolved, which callers treat as "keep".
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full) ?? string.Empty;
        var separators = new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar };
        var pending = new Stack<string>(
            full.Substring(root.Length).Split(separators, StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = root;
        var hops = 0;

        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = System.IO.Path.GetDirectoryName(current.TrimEnd(separators)) ?? root;
                if (current.Length < root.Length)
                {
                    current = root;
                }

                continue;
            }

            var next = System.IO.Path.Combine(current, part);
            var target = new System.IO.FileInfo(next).LinkTarget;
            if (target == null)
            {
                current = next;
                continue;
            }

            if (++hops > 40)
            {
                throw new System.IO.IOException("Too many levels of symbolic links: " + path);
            }

            var absolute = System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(current, target);
            absolute = System.IO.Path.GetFullPath(absolute);
            root = System.IO.Path.GetPathRoot(absolute) ?? root;
            current = root;
            foreach (var component in absolute.Substring(root.Length).Split(separators, StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                pending.Push(component);
            }
        }

        return current;
    }

    private static bool ContentMatches(string path, string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        using var stream = System.IO.File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) return false;
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), digest, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContentMatches(System.IO.Stream stream, string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        if (stream.Length > 64 * 1024 * 1024) return false;
        stream.Seek(0, System.IO.SeekOrigin.Begin);
        return string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), digest, StringComparison.OrdinalIgnoreCase);
    }

    private void CheckAndCleanSingle(Artist artist, Album single, Album? comparisonAlbumContext, ComparisonTrackSet comparisonTracks)
    {
        var albumTracks = comparisonTracks.StandardTracks(single.Id);
        var tier1OnlyTracks = comparisonTracks.Tier1OnlyTracks(single.Id);

        if (IsCandidateProtectedBySecondaryType(artist, single, comparisonAlbumContext))
        {
            return;
        }

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
            bool isTier1Redundant = TrackMatcher.IsTier1OnlyRedundant(singleTracks, albumTracks.Concat(tier1OnlyTracks).ToList());

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

                if (!MayUnmonitorFilelessSingle(artist, single))
                {
                    return;
                }

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

        // Whole-release protection (#17): evaluate all catalog tracks, including undownloaded
        // ones, so an exclusive track without files keeps the release (mirrors the stats path).
        var check = TrackMatcher.CheckSingle(singleTracks, albumTracks, tier1OnlyTracks, TrackMatcher.DurationToleranceMs);
        LogMatchDecision(artist, single, comparisonAlbumContext, check);

        if (!check.IsRedundant || IsUserRemonitored(artist, single))
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

    /// <summary>
    /// Loads downloaded comparison tracks grouped by source album id, so a candidate single's
    /// own tracks can be excluded when "Single" is a configured comparison type. Releases are
    /// classified by secondary type (see <see cref="ClassifySecondaryTypes"/>): excluded and
    /// unknown-type releases are not sources at all, and releases with any other secondary type
    /// are recorded as Tier-1-only sources.
    /// </summary>
    private ComparisonTrackSet GetComparisonTracksByAlbum(List<Album> albums, SingleCleanupOptions options)
    {
        var tracks = new ComparisonTrackSet();

        foreach (var album in albums.Where(a => options.ShouldCompareAgainstType(a.AlbumType)).Where(a => a.Monitored))
        {
            var secondaryClass = ClassifySecondaryTypes(album);
            if (secondaryClass == SecondaryTypeClass.Excluded || secondaryClass == SecondaryTypeClass.Unknown)
            {
                _logger.Debug(
                    "UniqueSingles comparison skip: album is not a reliable comparison source. albumId={0} album='{1}' secondaryTypes='{2}' reason={3}",
                    album.Id,
                    album.Title,
                    DescribeSecondaryTypes(album),
                    secondaryClass == SecondaryTypeClass.Excluded ? "excluded-secondary-type" : "secondary-types-unknown");
                continue;
            }

            try
            {
                var albumTracks = _trackService.GetTracksByAlbum(album.Id) ?? new List<Track>();
                tracks[album.Id] = albumTracks.Where(t => t.HasFile).ToList();
                if (secondaryClass == SecondaryTypeClass.Tier1Only)
                {
                    tracks.Tier1OnlyReleaseIds.Add(album.Id);
                    _logger.Debug(
                        "UniqueSingles comparison note: album carries a secondary type, so it contributes Tier-1 exact-MBID evidence only. albumId={0} album='{1}' secondaryTypes='{2}' reason=tier1-only-secondary-type",
                        album.Id,
                        album.Title,
                        DescribeSecondaryTypes(album));
                }
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

    private static bool NeedsReview(SingleRedundancyCheck check, Tier3Action action)
    {
        return action == Tier3Action.FlagOnly
            && !check.TrackResults.Any(r => r.Tier == MatchTier.NoMatch)
            && check.TrackResults.Any(r => r.Tier == MatchTier.Tier3_TitleOnly);
    }

    /// <summary>
    /// Orders candidate singles so review-needed candidates are analyzed first when exact-audio
    /// analysis is enabled. Audio comparison budgets are shared across the whole command/import;
    /// without prioritization, earlier candidates can exhaust the budget and leave review-queue
    /// releases unresolved (production finding 2026-10-29). Classification uses the same metadata
    /// check the cleanup path performs; a misprediction only affects ordering, never authority.
    /// </summary>
    private List<Album> OrderCandidatesForReviewPriority(List<Album> candidateSingles, ComparisonTrackSet comparisonTracks, SingleCleanupOptions options)
    {
        if (!options.ExactAudioAnalysis || candidateSingles.Count < 2)
        {
            return candidateSingles;
        }

        var prioritized = candidateSingles
            .Select(a => (Album: a, ReviewPriority: WouldFlagForReview(a, comparisonTracks, options)))
            .OrderByDescending(candidate => candidate.ReviewPriority) // stable: preserves order within each group
            .ToList();

        var reviewFirst = prioritized.Where(candidate => candidate.ReviewPriority).Select(candidate => candidate.Album.Id).ToList();
        if (reviewFirst.Count > 0 && prioritized[0].Album.Id != candidateSingles[0].Id)
        {
            _logger.Info(
                "UniqueSingles exact-audio priority: candidateReleaseIds={0} reason=review-priority-first",
                string.Join(",", reviewFirst));
        }

        return prioritized.Select(candidate => candidate.Album).ToList();
    }

    private bool WouldFlagForReview(Album single, ComparisonTrackSet comparisonTracks, SingleCleanupOptions options)
    {
        try
        {
            if (!single.Monitored || IsProtectedCandidate(single))
            {
                return false;
            }

            var singleTracks = _trackService.GetTracksByAlbum(single.Id) ?? new List<Track>();
            var downloadedSingleTracks = singleTracks.Where(t => t.HasFile).ToList();
            if (downloadedSingleTracks.Count == 0)
            {
                return false;
            }

            var albumTracks = comparisonTracks.StandardTracks(single.Id);
            var tier1OnlyTracks = comparisonTracks.Tier1OnlyTracks(single.Id);
            // Classification mirrors the authority check, which evaluates every catalog track
            // (#17): undownloaded exclusive tracks make the outcome keep-exclusive, not review.
            return NeedsReview(TrackMatcher.CheckSingle(singleTracks, albumTracks, tier1OnlyTracks, options.DurationToleranceMs), options.Tier3Action);
        }
        catch (Exception)
        {
            // Classification is best-effort: the authoritative decision is recomputed per candidate.
            return false;
        }
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

    /// <summary>
    /// Gate for every file-less unmonitor (Tier-1 and Tier-2, scan and import). A single with a
    /// grabbed, downloading, or awaiting-import release is file-less only until that download
    /// imports; unmonitoring it now would land the files in an unmonitored release. It stays
    /// monitored, so the import-time self-check cleans the files as a normal monitored single.
    /// </summary>
    private bool MayUnmonitorFilelessSingle(Artist artist, Album single)
    {
        if (IsUserRemonitored(artist, single))
        {
            return false;
        }

        if (_inFlightProbe.IsInFlight(single, out var detail))
        {
            _logger.Info(
                "UniqueSingles unmonitor deferred: single has a download Lidarr can still import; it stays monitored so the import-time cleanup handles the files. artistId={0} artist='{1}' singleId={2} single='{3}' download='{4}' reason=download-in-flight",
                artist.Id,
                artist.Name,
                single.Id,
                single.Title,
                detail);
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the user took the release back after the plugin unmonitored it (or provenance
    /// cannot be read). Such a release is never unmonitored or deleted by the plugin again.
    /// </summary>
    private bool IsUserRemonitored(Artist artist, Album single)
    {
        var provenance = _historyRecorder.GetProvenance(single.Id);
        if (provenance != UnmonitorProvenance.UserRemonitored && provenance != UnmonitorProvenance.Unknown)
        {
            return false;
        }

        _logger.Info(
            "UniqueSingles cleanup skip: user re-monitored this release after the plugin unmonitored it. artistId={0} artist='{1}' singleId={2} single='{3}' provenance={4} reason=user-remonitored",
            artist.Id,
            artist.Name,
            single.Id,
            single.Title,
            provenance);
        return true;
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
            // Lidarr History audit row; recorder failures never affect the cleanup decision.
            try
            {
                _historyRecorder.RecordUnmonitor(artist, single, check.SummaryReason);
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

    /// <summary>
    /// Deletes a release's files. When <paramref name="validate"/> is supplied (exact-audio
    /// cleanup only), each deletion is guarded: the file is opened with write sharing denied,
    /// its verified content identity is rechecked through that same handle, the handle is held
    /// across the destructive call, and the pinned bytes are re-read afterwards so any in-place
    /// mutation between the final check and deletion is detected and aborts the remaining
    /// deletions. On Windows the share mode blocks in-place writers outright; elsewhere the
    /// re-read detects them. A rename-over of the path remains an inherent OS-level TOCTOU
    /// because deletion goes through Lidarr's path-based deletion service; see
    /// docs/exact-flac-analysis.md.
    /// The retained source is anchored by the complementary check: after each destructive
    /// call <paramref name="verifySurvivors"/> re-confirms the source still exists with its
    /// verified content, and any failure aborts the remaining deletions (fail closed). A source
    /// removed inside the destructive window cannot be un-deleted, but it is never silent.
    /// </summary>
    private bool TryDeleteSingleFiles(Artist artist, Album single, Album? comparisonAlbumContext,
        Func<TrackFile, System.IO.FileStream?, string?>? validate = null,
        Func<TrackFile, string?>? verifySurvivors = null)
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
            // The delete-guard handle anchors content identity at deletion time: open before
            // revalidation, verify through it, and keep it open across the destructive call.
            System.IO.FileStream? guard = null;
            try
            {
                var verifiedDigest = default(string);
                if (validate != null)
                {
                    try
                    {
                        guard = new System.IO.FileStream(file.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read | System.IO.FileShare.Delete);
                    }
                    catch (Exception openEx)
                    {
                        _logger.Warn(
                            openEx,
                            "UniqueSingles exact-audio cleanup: candidateReleaseId={0} trackFileId={1} decision=keep reason=delete-guard-open-failed path-unverifiable-at-delete-time remainingDeletions=aborted",
                            single.Id,
                            file.Id);
                        return false;
                    }

                    verifiedDigest = validate(file, guard);
                    if (verifiedDigest == null)
                    {
                        return false;
                    }
                }

                _deleteMediaFiles.DeleteTrackFile(artist, file);

                if (guard != null && !DeletedContentStillMatches(guard, verifiedDigest, single.Id, file.Id))
                {
                    return false;
                }

                // Survivor anchor: the retained source is the only copy left once this file is
                // gone, so re-confirm it still exists with the verified content before any
                // further deletion. A removal or replacement racing the destructive call cannot
                // be undone, but it is detected here, reported loudly, and stops the rest.
                var survivorFailure = verifySurvivors?.Invoke(file);
                if (survivorFailure != null)
                {
                    _logger.Warn(
                        "UniqueSingles exact-audio cleanup: candidateReleaseId={0} trackFileId={1} decision=keep reason={2} detail=retained-source-no-longer-matches-verified-evidence remainingDeletions=aborted",
                        single.Id,
                        file.Id,
                        survivorFailure);
                    return false;
                }

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
            finally
            {
                guard?.Dispose();
            }
        }

        if (allDeleted)
        {
            // The audio is gone, so take the recognized sidecars (NFO, cover art, lyrics) and the
            // folder with it. Lidarr's own empty-folder cleanup skips the folder while sidecars
            // keep it non-empty. Failures never change the cleanup result.
            try
            {
                _folderCleanup.RemoveLeftovers(artist, single, files);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "UniqueSingles folder cleanup failure: artistId={0} singleId={1} single='{2}' reason=folder-cleanup-failed",
                    artist.Id,
                    single.Id,
                    single.Title);
            }
        }

        return allDeleted;
    }

    /// <summary>
    /// Re-reads the pinned delete-guard handle after the destructive call and compares it with
    /// the digest verified immediately before deletion. In-place mutation of the deleted file
    /// between the final check and deletion is therefore detected rather than silently consumed.
    /// Any failure (including an unreadable handle) fails closed and aborts remaining deletions.
    /// </summary>
    private bool DeletedContentStillMatches(System.IO.FileStream deleted, string? verifiedDigest, int releaseId, int trackFileId)
    {
        try
        {
            if (ContentMatches(deleted, verifiedDigest))
            {
                return true;
            }

            _logger.Warn(
                "UniqueSingles exact-audio cleanup: candidateReleaseId={0} trackFileId={1} decision=keep reason=mutation-detected-post-delete detail=deleted-content-no-longer-matches-verified-evidence remainingDeletions=aborted",
                releaseId,
                trackFileId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles exact-audio cleanup: candidateReleaseId={0} trackFileId={1} decision=keep reason=post-delete-verify-failed detail=deleted-content-could-not-be-rechecked remainingDeletions=aborted",
                releaseId,
                trackFileId);
            return false;
        }
    }

    private static SingleCleanupOptions CreateLegacyOptions()
    {
        return new SingleCleanupOptions(
            3000,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album", "EP" },
            Tier3Action.FlagOnly);
    }

    /// <summary>
    /// Secondary-type handling shared by the cleanup, import, self-check, review-ordering,
    /// exact-audio revalidation and eager paths. Lidarr stores secondary types separately from
    /// the primary type, so a release can be an "Album" and also be Live, a Compilation, etc.
    /// </summary>
    internal enum SecondaryTypeClass
    {
        /// <summary>No secondary type, or only Studio: an ordinary release.</summary>
        Standard,

        /// <summary>
        /// Carries a secondary type that is not excluded (Compilation, Soundtrack, Spokenword,
        /// Interview, Audiobook, Mixtape/Street, Audio drama). As a comparison source it may
        /// only contribute Tier-1 (exact recording MBID) evidence: a re-recorded hit or radio
        /// edit on a "Greatest Hits" shares the bare title and a near-equal length with the
        /// studio single, so Tier 2 and Tier 3 are unreliable there.
        /// </summary>
        Tier1Only,

        /// <summary>
        /// Live, Remix, DJ-mix or Demo (<see cref="SingleCleanupService.ExcludedSecondaryTypeIds"/>).
        /// Never a comparison source, and as a candidate never unmonitored or deleted by this
        /// plugin.
        /// </summary>
        Excluded,

        /// <summary>
        /// <c>SecondaryTypes</c> is null: not loaded, or stale metadata. Lidarr's Album
        /// constructor initialises it to an empty list, so null never means "no secondary
        /// types". Treated as the conservative side of both roles: not a source, and a
        /// protected candidate.
        /// </summary>
        Unknown,
    }

    internal static SecondaryTypeClass ClassifySecondaryTypes(Album album)
    {
        // A null list, or a null element inside it, is unreadable data rather than "none".
        if (album.SecondaryTypes == null || album.SecondaryTypes.Any(t => t == null))
        {
            return SecondaryTypeClass.Unknown;
        }

        if (album.SecondaryTypes.Any(t => ExcludedSecondaryTypeIds.Contains(t.Id)))
        {
            return SecondaryTypeClass.Excluded;
        }

        return album.SecondaryTypes.Any(t => t.Id != SecondaryAlbumType.Studio.Id)
            ? SecondaryTypeClass.Tier1Only
            : SecondaryTypeClass.Standard;
    }

    /// <summary>
    /// Releases whose tracks are usually different recordings that share the studio title
    /// (live, remix, DJ-mix, demo). A title + duration match against them is not evidence
    /// that the single is redundant, so they are never used as comparison sources, and (the
    /// mirror image) a single carrying one of these types is never itself a cleanup candidate.
    /// Lidarr stores these as secondary types; the primary type is still "Album" or "EP".
    /// Internal so the eager pre-grab path applies the identical exclusion.
    /// </summary>
    internal static bool HasExcludedSecondaryType(Album album)
    {
        return ClassifySecondaryTypes(album) == SecondaryTypeClass.Excluded;
    }

    /// <summary>
    /// True when a single must be left alone because of its own secondary types: an excluded
    /// type (Live, Remix, DJ-mix, Demo) or an unreadable (null) secondary-type list. Such a
    /// single is a distinct version rather than a duplicate, so it is never unmonitored or
    /// deleted on any evidence tier, Tier 1 included.
    /// </summary>
    internal static bool IsProtectedCandidate(Album single)
    {
        var secondaryClass = ClassifySecondaryTypes(single);
        return secondaryClass == SecondaryTypeClass.Excluded || secondaryClass == SecondaryTypeClass.Unknown;
    }

    internal static string DescribeSecondaryTypes(Album album)
    {
        return album.SecondaryTypes == null
            ? "<null>"
            : string.Join(",", album.SecondaryTypes.Select(t => t?.Name ?? "<null>"));
    }

    private bool IsCandidateProtectedBySecondaryType(Artist artist, Album single, Album? comparisonAlbumContext)
    {
        if (!IsProtectedCandidate(single))
        {
            return false;
        }

        _logger.Info(
            "UniqueSingles cleanup skip: single carries a secondary type that marks a distinct version, or its secondary types are unknown; it is never unmonitored or deleted. artistId={0} artist='{1}' singleId={2} single='{3}' secondaryTypes='{4}' comparisonAlbumId={5} comparisonAlbum='{6}' reason={7}",
            artist.Id,
            artist.Name,
            single.Id,
            single.Title,
            DescribeSecondaryTypes(single),
            comparisonAlbumContext?.Id,
            comparisonAlbumContext?.Title,
            single.SecondaryTypes == null ? "candidate-secondary-types-unknown" : "candidate-excluded-secondary-type");
        return true;
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

/// <summary>
/// Downloaded comparison tracks keyed by source release id. Releases listed in
/// <see cref="Tier1OnlyReleaseIds"/> carry a secondary type and may only contribute Tier-1
/// (exact recording MBID) matches; every other release also contributes Tier 2 and Tier 3.
/// </summary>
internal sealed class ComparisonTrackSet : Dictionary<int, List<Track>>
{
    public HashSet<int> Tier1OnlyReleaseIds { get; } = new();

    /// <summary>Tracks eligible for Tier 1, 2 and 3, never the candidate's own.</summary>
    public List<Track> StandardTracks(int candidateReleaseId) => this
        .Where(entry => entry.Key != candidateReleaseId && !Tier1OnlyReleaseIds.Contains(entry.Key))
        .SelectMany(entry => entry.Value)
        .ToList();

    /// <summary>Tracks eligible for Tier 1 only, never the candidate's own.</summary>
    public List<Track> Tier1OnlyTracks(int candidateReleaseId) => this
        .Where(entry => entry.Key != candidateReleaseId && Tier1OnlyReleaseIds.Contains(entry.Key))
        .SelectMany(entry => entry.Value)
        .ToList();
}
