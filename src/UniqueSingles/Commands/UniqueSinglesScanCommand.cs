using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Command to scan the entire library for redundant single releases.
/// Triggered manually via Lidarr UI Tasks, runs through IExecute handler.
/// Safe/idempotent: re-running a scan on already-cleaned artists is a no-op.
/// </summary>
public class UniqueSinglesScanCommand : Command
{
    public UniqueSinglesScanCommand()
    {
    }

    /// <summary>
    /// Optional artist filter for targeted scans. If null or 0, scans all monitored artists.
    /// </summary>
    public int? ArtistId { get; set; }

    /// <summary>
    /// Indicates whether this is a dry run that would only log without making changes.
    /// Currently always false; placeholder for future UI enhancement.
    /// </summary>
    public bool DryRun { get; set; } = false;
}
