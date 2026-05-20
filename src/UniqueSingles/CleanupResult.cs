using System;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Summary statistics from a cleanup or scan operation.
/// </summary>
public readonly struct CleanupResult
{
    /// <summary>
    /// Number of candidate singles checked for redundancy.
    /// </summary>
    public int CandidatesChecked { get; }

    /// <summary>
    /// Number of singles successfully cleaned (unmonitored and deleted).
    /// </summary>
    public int Cleaned { get; }

    /// <summary>
    /// Number of singles skipped (not redundant, already unmonitored, no downloaded tracks, etc.).
    /// </summary>
    public int Skipped { get; }

    /// <summary>
    /// Number of singles that have Tier 3 matches only and require manual review.
    /// </summary>
    public int ReviewNeeded { get; }

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
        int deleteFailures)
    {
        CandidatesChecked = Math.Max(0, candidatesChecked);
        Cleaned = Math.Max(0, cleaned);
        Skipped = Math.Max(0, skipped);
        ReviewNeeded = Math.Max(0, reviewNeeded);
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
            left.DeleteFailures + right.DeleteFailures);
    }
}