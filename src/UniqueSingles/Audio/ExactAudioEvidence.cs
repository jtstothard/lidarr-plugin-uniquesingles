using System;
using System.Threading;
using System.Collections.Generic;

namespace NzbDrone.Core.Plugins;

public enum AudioEvidenceStatus
{
    VerifiedExact,
    Different,
    Unavailable,
}

/// <summary>Immutable evidence from a complete decode, never deletion authority. Policy includes PCM representation.</summary>
public sealed record DecodedAudio(string FileDigest, string PcmDigest, int SampleRate, int Channels, int BitDepth, long Samples, string ChannelLayout)
{
    public double DurationMs => Samples * 1000.0 / SampleRate;
}

public sealed record AudioEvidence(AudioEvidenceStatus Status, string Reason, DecodedAudio? Candidate = null, DecodedAudio? Source = null)
{
    public const string Policy = "flac-original-packed-le-sha256-v1";
}

public interface IExactAudioVerifier
{
    AudioEvidence Compare(string candidatePath, string sourcePath, CancellationToken cancellationToken);
}

/// <summary>
/// Shared by one options snapshot (whole command/import), including every artist. Cache hits also
/// consume comparison work. The total is split into a shared lane every candidate may use and a
/// review lane reserved for review-priority candidates, so earlier candidates cannot starve the
/// review queue of comparison attempts.
/// </summary>
public sealed class AudioAnalysisBudget
{
    public const int DefaultComparisons = 64;
    public const int DefaultReviewReserve = 32;

    private readonly int _reviewReserve;
    private int _remaining;

    public AudioAnalysisBudget(int comparisons = DefaultComparisons, int reviewReserve = DefaultReviewReserve)
    {
        _remaining = Math.Clamp(comparisons, 0, DefaultComparisons);
        _reviewReserve = Math.Clamp(reviewReserve, 0, _remaining);
    }

    public bool TryTake(bool reviewPriority = false)
    {
        // Decrement first, then validate, mirroring the original atomic pattern; roll back when
        // the take would cross into the reserved review lane (or past exhaustion).
        var remaining = Interlocked.Decrement(ref _remaining);
        if (remaining >= 0 && (reviewPriority || remaining >= _reviewReserve))
        {
            return true;
        }

        Interlocked.Increment(ref _remaining);
        return false;
    }
}

/// <summary>Track and file identities are evidence about a snapshot, never a reusable cleanup permit.</summary>
public sealed record AudioTrackEvidence(int CandidateReleaseId, int CandidateTrackId, int? SourceReleaseId,
    int? SourceTrackId, string? CandidatePath, string? SourcePath, AudioEvidence Evidence);

public sealed record ExactAudioReleaseReport(int CandidateReleaseId, bool Complete, IReadOnlyList<AudioTrackEvidence> Tracks)
{
    public string Decision => Complete ? "report-only-exact-duplicate" : "unresolved-audio";
}
