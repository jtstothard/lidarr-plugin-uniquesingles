using System.Collections.Generic;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Represents the redundancy check result for an entire single release.
/// A single is only considered redundant if ALL its tracks have matches
/// on monitored albums/EPs (multi-track protection rule).
/// </summary>
/// <param name="IsRedundant">True only if every track on the single has a match.</param>
/// <param name="TrackResults">Match results for each individual track on the single.</param>
/// <param name="SummaryReason">Human-readable summary of why the single is or isn't redundant.</param>
public record SingleRedundancyCheck(
    bool IsRedundant,
    List<MatchResult> TrackResults,
    string SummaryReason);
