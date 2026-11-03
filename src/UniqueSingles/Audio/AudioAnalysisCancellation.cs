using System;
using System.Threading;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Process-wide cancellation signal for exact-audio analysis. Lidarr's synchronous plugin
/// entry points (IExecute&lt;UniqueSinglesScanCommand&gt;.Execute and NotificationBase.OnReleaseImport)
/// do not receive a per-command dispatcher cancellation token; the dispatcher lifecycle signal a
/// plugin can observe is ApplicationShutdownRequested, which Lidarr's own CommandExecutor also
/// handles. This hub exposes that signal as a CancellationToken so scan and import entry points
/// can link it into SingleCleanupOptions.AudioCancellation.
/// </summary>
public interface IAudioAnalysisCancellation
{
    /// <summary>Token cancelled when the Lidarr dispatcher signals shutdown.</summary>
    CancellationToken Token { get; }
}

public sealed class AudioAnalysisCancellation : IAudioAnalysisCancellation, IHandle<ApplicationShutdownRequested>
{
    private readonly CancellationTokenSource _source = new();
    private readonly Logger _logger;

    public AudioAnalysisCancellation(Logger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CancellationToken Token => _source.Token;

    public void Handle(ApplicationShutdownRequested message)
    {
        try
        {
            _source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Host is tearing down; nothing left to cancel.
        }

        _logger.Info("UniqueSingles audio analysis cancellation: reason=dispatcher-shutdown");
    }
}
