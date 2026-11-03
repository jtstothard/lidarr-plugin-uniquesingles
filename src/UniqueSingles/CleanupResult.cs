using System;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Summary statistics from a cleanup or scan operation.
/// </summary>
public readonly struct CleanupResult
{
    /// <summary>
    /// Number of fully verified exact-audio duplicate releases reported without cleanup.
    /// </summary>
    public int ExactAudioReports { get; }

    /// <summary>Number of candidate singles checked for redundancy.</summary>
    public int CandidatesChecked { get; }

    /// <summary>
    /// Number of singles successfully unmonitored (cleanup-approved). Files are deleted after
    /// unmonitoring, but deletion may legitimately not have occurred (no files, or a delete
    /// failure recorded in <see cref="DeleteFailures"/>).
    /// </summary>
    public int Cleaned { get; }

    /// <summary>
    /// Number of singles skipped (not redundant, already unmonitored, no downloaded tracks, etc.).
    /// </summary>
    public int Skipped { get; }

    /// <summary>
    /// Number of unresolved singles requiring manual review, counted once per release.
    /// Releases protected by an unmatched/exclusive track are excluded.
    /// </summary>
    public int ReviewNeeded { get; }

    /// <summary>
    /// Number of singles kept because they contain content with no counterpart anywhere in the
    /// comparison pool (outcome=keep-exclusive). Reported separately from <see cref="Skipped"/>
    /// so summary counters reconcile with per-release outcome lines.
    /// </summary>
    public int KeepExclusive { get; }

    /// <summary>
    /// Number of unmonitor failures.
    /// </summary>
    public int UnmonitorFailures { get; }

    /// <summary>
    /// Number of delete failures.
    /// </summary>
    public int DeleteFailures { get; }

    public CleanupResult(
        int candidatesChecked,
        int cleaned,
        int skipped,
        int reviewNeeded,
        int unmonitorFailures,
        int deleteFailures,
        int exactAudioReports = 0,
        int keepExclusive = 0)
    {
        ExactAudioReports = Math.Max(0, exactAudioReports);
        CandidatesChecked = Math.Max(0, candidatesChecked);
        Cleaned = Math.Max(0, cleaned);
        Skipped = Math.Max(0, skipped);
        ReviewNeeded = Math.Max(0, reviewNeeded);
        KeepExclusive = Math.Max(0, keepExclusive);
        UnmonitorFailures = Math.Max(0, unmonitorFailures);
        DeleteFailures = Math.Max(0, deleteFailures);
    }

    /// <summary>
    /// Creates an empty result.
    /// </summary>
    public static CleanupResult Empty => new();

    /// <summary>
    /// Adds two results together.
    /// </summary>
    public static CleanupResult operator +(CleanupResult left, CleanupResult right)
    {
        return new CleanupResult(
            left.CandidatesChecked + right.CandidatesChecked,
            left.Cleaned + right.Cleaned,
            left.Skipped + right.Skipped,
            left.ReviewNeeded + right.ReviewNeeded,
            left.UnmonitorFailures + right.UnmonitorFailures,
            left.DeleteFailures + right.DeleteFailures,
            left.ExactAudioReports + right.ExactAudioReports,
            left.KeepExclusive + right.KeepExclusive);
    }
}