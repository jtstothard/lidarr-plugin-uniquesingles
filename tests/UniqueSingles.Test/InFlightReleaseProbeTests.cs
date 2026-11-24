using System.Reflection;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

public class InFlightReleaseProbeTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Album Single = new() { Id = 200, Title = "Hit Single", AlbumType = "Single" };

    [Theory]
    [InlineData(TrackedDownloadState.Downloading, true)]
    [InlineData(TrackedDownloadState.ImportPending, true)]
    [InlineData(TrackedDownloadState.Importing, true)]
    [InlineData(TrackedDownloadState.ImportBlocked, true)]
    [InlineData(TrackedDownloadState.ImportFailed, true)]
    [InlineData(TrackedDownloadState.Imported, false)]
    [InlineData(TrackedDownloadState.Ignored, false)]
    [InlineData(TrackedDownloadState.DownloadFailed, false)]
    [InlineData(TrackedDownloadState.DownloadFailedPending, false)]
    public void TrackedDownloadForRelease_InFlightUnlessTerminal(TrackedDownloadState state, bool expected)
    {
        var tracked = TrackedProxy.Create();
        tracked.Downloads.Add(Tracked("dl-1", state, Single.Id));
        var probe = Probe(tracked, HistoryProxy.Create());

        Assert.Equal(expected, probe.IsInFlight(Single, out _));
    }

    [Fact]
    public void TrackedDownloadForOtherRelease_IsNotInFlight()
    {
        var tracked = TrackedProxy.Create();
        tracked.Downloads.Add(Tracked("dl-1", TrackedDownloadState.Downloading, albumId: 999));

        Assert.False(Probe(tracked, HistoryProxy.Create()).IsInFlight(Single, out _));
    }

    [Fact]
    public void RecentGrabNotYetTracked_IsInFlight()
    {
        // Lidarr writes the Grabbed row at grab time but only tracks the download after its
        // next debounced refresh.
        var history = HistoryProxy.Create();
        history.Rows.Add(Grab("dl-1", Now.AddMinutes(-1)));

        Assert.True(Probe(TrackedProxy.Create(), history).IsInFlight(Single, out var detail));
        Assert.Contains("recent-grab-not-yet-tracked", detail);
    }

    [Theory]
    [InlineData(EntityHistoryEventType.DownloadImported)]
    [InlineData(EntityHistoryEventType.DownloadFailed)]
    [InlineData(EntityHistoryEventType.DownloadIgnored)]
    public void RecentGrabWithTerminalHistory_IsNotInFlight(EntityHistoryEventType terminal)
    {
        var history = HistoryProxy.Create();
        history.Rows.Add(Grab("dl-1", Now.AddMinutes(-30)));
        history.Rows.Add(new EntityHistory { AlbumId = Single.Id, DownloadId = "dl-1", EventType = terminal, Date = Now.AddMinutes(-5) });

        Assert.False(Probe(TrackedProxy.Create(), history).IsInFlight(Single, out _));
    }

    [Fact]
    public void UntrackedGrabOlderThanWindow_IsNotInFlight()
    {
        // A download removed from the client outside Lidarr must not block the unmonitor forever.
        var history = HistoryProxy.Create();
        history.Rows.Add(Grab("dl-1", Now - LidarrInFlightReleaseProbe.UntrackedGrabWindow - TimeSpan.FromMinutes(1)));

        Assert.False(Probe(TrackedProxy.Create(), history).IsInFlight(Single, out _));
    }

    [Fact]
    public void GrabTrackedUnderDifferentMapping_UsesTrackedState()
    {
        // The grab history names this release even though the parsed queue mapping did not.
        var tracked = TrackedProxy.Create();
        tracked.Downloads.Add(Tracked("dl-1", TrackedDownloadState.Downloading, albumId: 999));
        var history = HistoryProxy.Create();
        history.Rows.Add(Grab("dl-1", Now.AddDays(-3)));

        Assert.True(Probe(tracked, history).IsInFlight(Single, out _));
    }

    [Fact]
    public void LookupFailure_FailsSafeAsInFlight()
    {
        var tracked = TrackedProxy.Create();
        tracked.Throw = true;

        Assert.True(Probe(tracked, HistoryProxy.Create()).IsInFlight(Single, out var detail));
        Assert.Equal("lookup-failed", detail);
    }

    private static LidarrInFlightReleaseProbe Probe(TrackedProxy tracked, HistoryProxy history) =>
        new(
            new Lazy<ITrackedDownloadService>(() => (ITrackedDownloadService)(object)tracked),
            new Lazy<IHistoryService>(() => (IHistoryService)(object)history),
            () => Now,
            LogManager.CreateNullLogger());

    private static TrackedDownload Tracked(string downloadId, TrackedDownloadState state, int albumId) => new()
    {
        DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = "Release" },
        State = state,
        RemoteAlbum = new RemoteAlbum { Albums = new List<Album> { new() { Id = albumId } } },
    };

    private static EntityHistory Grab(string downloadId, DateTime date) => new()
    {
        AlbumId = Single.Id,
        DownloadId = downloadId,
        EventType = EntityHistoryEventType.Grabbed,
        Date = date,
    };

    public class TrackedProxy : DispatchProxy
    {
        public List<TrackedDownload> Downloads { get; } = new();

        public bool Throw { get; set; }

        public static TrackedProxy Create() => (TrackedProxy)(object)Create<ITrackedDownloadService, TrackedProxy>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Throw)
            {
                throw new InvalidOperationException("tracked downloads unavailable");
            }

            return method!.Name switch
            {
                nameof(ITrackedDownloadService.GetTrackedDownloads) => Downloads,
                nameof(ITrackedDownloadService.Find) => Downloads.FirstOrDefault(d => d.DownloadItem.DownloadId == (string)args![0]!),
                _ => throw new NotSupportedException($"Probe must stay read-only; called {method.Name}."),
            };
        }
    }

    public class HistoryProxy : DispatchProxy
    {
        public List<EntityHistory> Rows { get; } = new();

        public static HistoryProxy Create() => (HistoryProxy)(object)Create<IHistoryService, HistoryProxy>();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method!.Name switch
            {
                nameof(IHistoryService.GetByAlbum) => Rows
                    .Where(h => h.AlbumId == (int)args![0]!)
                    .Where(h => args[1] == null || h.EventType == (EntityHistoryEventType)args[1]!)
                    .ToList(),
                nameof(IHistoryService.FindByDownloadId) => Rows.Where(h => h.DownloadId == (string)args![0]!).ToList(),
                _ => throw new NotSupportedException($"Probe must stay read-only; called {method.Name}."),
            };
        }
    }
}
