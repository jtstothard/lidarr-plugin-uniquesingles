using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins;

/// <summary>Whole-release report only. Neither this class nor audio evidence can invoke cleanup.</summary>
public sealed class ExactAudioReleaseReporter
{
    private readonly IMediaFileService _files;
    private readonly Logger _logger;
    private readonly IExactAudioVerifier _verifier;

    public ExactAudioReleaseReporter(IMediaFileService files, Logger logger, IExactAudioVerifier verifier)
    {
        _files = files;
        _logger = logger;
        _verifier = verifier;
    }

    public ExactAudioReleaseReport Analyze(Album single, List<Track> requiredTracks, Dictionary<int, List<Track>> sources, SingleCleanupOptions options)
    {
        var complete = requiredTracks.Count > 0;
        var attempts = new List<AudioTrackEvidence>();
        foreach (var candidate in requiredTracks)
        {
            var verified = false;
            var hadCandidate = false;
            var title = TitleNormalizer.Normalize(candidate.Title);
            var plausible = sources.Where(entry => entry.Key != single.Id)
                .SelectMany(entry => entry.Value.Select(track => (ReleaseId: entry.Key, Track: track)))
                .Where(item => item.Track.HasFile
                && ((!string.IsNullOrWhiteSpace(candidate.ForeignRecordingId) && candidate.ForeignRecordingId == item.Track.ForeignRecordingId)
                    || (title.Length > 0 && title == TitleNormalizer.Normalize(item.Track.Title))));
            foreach (var item in plausible)
            {
                hadCandidate = true;
                var source = item.Track;
                string? candidatePath = null, sourcePath = null;
                AudioEvidence evidence;
                if (!candidate.HasFile)
                    evidence = new AudioEvidence(AudioEvidenceStatus.Unavailable, "required-candidate-file-missing");
                else if (options.AudioCancellation.IsCancellationRequested)
                    evidence = new AudioEvidence(AudioEvidenceStatus.Unavailable, "cancelled");
                else if (!options.AudioBudget.TryTake())
                    evidence = new AudioEvidence(AudioEvidenceStatus.Unavailable, "comparison-budget-exhausted");
                else
                {
                    try
                    {
                        var candidateFile = _files.GetFilesByAlbum(single.Id)?.SingleOrDefault(f => f.Id == candidate.TrackFileId);
                        var sourceFile = _files.GetFilesByAlbum(item.ReleaseId)?.SingleOrDefault(f => f.Id == source.TrackFileId);
                        candidatePath = candidateFile?.Path;
                        sourcePath = sourceFile?.Path;
                        evidence = candidateFile == null || sourceFile == null
                            ? new AudioEvidence(AudioEvidenceStatus.Unavailable, "media-file-missing")
                            : _verifier.Compare(candidateFile.Path, sourceFile.Path, options.AudioCancellation);
                    }
                    catch (Exception)
                    {
                        evidence = new AudioEvidence(AudioEvidenceStatus.Unavailable, "media-file-lookup-failed");
                    }
                }

                attempts.Add(new AudioTrackEvidence(single.Id, candidate.Id, item.ReleaseId, source.Id, candidatePath, sourcePath, evidence));
                _logger.Info("UniqueSingles exact-audio: candidateReleaseId={0} candidateTrackId={1} candidateRecordingId='{2}' sourceReleaseId={3} sourceTrackId={4} sourceRecordingId='{5}' candidateCatalogDurationMs={6} sourceCatalogDurationMs={7} candidateActualDurationMs={8} sourceActualDurationMs={9} method={10} status={11} reason={12} candidateFileDigest={13} sourceFileDigest={14} candidatePcmDigest={15} sourcePcmDigest={16}",
                    single.Id, candidate.Id, candidate.ForeignRecordingId, item.ReleaseId, source.Id, item.Track.ForeignRecordingId,
                    candidate.Duration, source.Duration, evidence.Candidate?.DurationMs, evidence.Source?.DurationMs,
                    AudioEvidence.Policy, evidence.Status, evidence.Reason, evidence.Candidate?.FileDigest, evidence.Source?.FileDigest, evidence.Candidate?.PcmDigest, evidence.Source?.PcmDigest);
                if (evidence.Status == AudioEvidenceStatus.VerifiedExact)
                {
                    verified = true;
                    break;
                }
                if (!candidate.HasFile || options.AudioCancellation.IsCancellationRequested || evidence.Reason == "comparison-budget-exhausted") break;
            }
            if (!hadCandidate)
            {
                attempts.Add(new AudioTrackEvidence(single.Id, candidate.Id, null, null, null, null,
                    new AudioEvidence(AudioEvidenceStatus.Unavailable, candidate.HasFile ? "no-eligible-title-or-recording-source" : "required-candidate-file-missing")));
                _logger.Info("UniqueSingles exact-audio: candidateReleaseId={0} candidateTrackId={1} status=Unavailable reason=no-eligible-title-or-recording-source", single.Id, candidate.Id);
            }
            if (!verified) complete = false;
        }

        var report = new ExactAudioReleaseReport(single.Id, complete, attempts.AsReadOnly());
        _logger.Info("UniqueSingles exact-audio release: candidateReleaseId={0} decision={1} requiredTracks={2} cleanupAuthority=none",
            single.Id, report.Decision, requiredTracks.Count);
        return report;
    }
}
