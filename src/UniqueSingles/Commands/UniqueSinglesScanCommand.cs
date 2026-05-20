using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Command to scan the entire library for redundant single releases.
/// Triggered manually via Lidarr UI Tasks or by the scheduler through UniqueSinglesScanTask.
/// Safe/idempotent: re-running a scan on already-cleaned artists is a no-op.
/// </summary>
public class UniqueSinglesScanCommand : Command
{
    private string _resultMessage;

    public UniqueSinglesScanCommand()
    {
        SendUpdatesToClient = true;
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

    /// <summary>
    /// Mutable result message populated by the executor after scan completion.
    /// Read by CompletionMessage to display as a toast in the Lidarr UI.
    /// </summary>
    public string ResultMessage
    {
        get => _resultMessage;
        set => _resultMessage = value;
    }

    /// <summary>
    /// Override that returns the scan result summary for toast display.
    /// The CommandExecutor reads this after Execute() completes.
    /// </summary>
    public override string CompletionMessage => _resultMessage;
}
