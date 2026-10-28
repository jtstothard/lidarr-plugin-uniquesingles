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

/// <summary>Shared by one options snapshot (whole command/import), including every artist. Cache hits also consume comparison work.</summary>
public sealed class AudioAnalysisBudget
{
    private int _remaining;

    public AudioAnalysisBudget(int comparisons = 32) => _remaining = Math.Clamp(comparisons, 0, 32);

    public bool TryTake() => Interlocked.Decrement(ref _remaining) >= 0;
}

/// <summary>Track and file identities are evidence about a snapshot, never a reusable cleanup permit.</summary>
public sealed record AudioTrackEvidence(int CandidateReleaseId, int CandidateTrackId, int? SourceReleaseId,
    int? SourceTrackId, string? CandidatePath, string? SourcePath, AudioEvidence Evidence);

public sealed record ExactAudioReleaseReport(int CandidateReleaseId, bool Complete, IReadOnlyList<AudioTrackEvidence> Tracks)
{
    public string Decision => Complete ? "report-only-exact-duplicate" : "unresolved-audio";
}
