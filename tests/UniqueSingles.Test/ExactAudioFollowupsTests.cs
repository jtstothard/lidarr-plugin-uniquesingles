using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Core.History;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Focused unit tests for the issue #44 follow-ups: the dispatcher cancellation hub, the
/// review-priority comparison budget lanes, and the Lidarr History audit recorder.
/// </summary>
public class ExactAudioFollowupsTests
{
    // ── AudioAnalysisCancellation hub ─────────────────────────────────

    [Fact]
    public void AudioAnalysisCancellation_CancelsTokenOnShutdownRequested()
    {
        using var loggerHarness = new LoggerHarness();
        var hub = new AudioAnalysisCancellation(loggerHarness.Logger);

        Assert.False(hub.Token.IsCancellationRequested);

        hub.Handle(new NzbDrone.Core.Lifecycle.ApplicationShutdownRequested());

        Assert.True(hub.Token.IsCancellationRequested);
        // Repeated shutdown signals stay idempotent.
        hub.Handle(new NzbDrone.Core.Lifecycle.ApplicationShutdownRequested());
        Assert.True(hub.Token.IsCancellationRequested);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("reason=dispatcher-shutdown", StringComparison.Ordinal));
    }

    // ── AudioAnalysisBudget lanes ─────────────────────────────────────

    [Fact]
    public void AudioAnalysisBudget_ReservesComparisonLaneForReviewPriority()
    {
        var budget = new AudioAnalysisBudget(4, 2);

        Assert.True(budget.TryTake());   // shared lane
        Assert.True(budget.TryTake());   // shared lane drained
        Assert.False(budget.TryTake());  // non-review cannot cross into the reserve
        Assert.True(budget.TryTake(reviewPriority: true));   // review may use the reserve
        Assert.True(budget.TryTake(reviewPriority: true));   // fully drained
        Assert.False(budget.TryTake(reviewPriority: true));
        Assert.False(budget.TryTake());  // still exhausted for non-review
    }

    [Fact]
    public void AudioAnalysisBudget_DefaultSplitsSixtyFourComparisonsWithThirtyTwoReserved()
    {
        var budget = new AudioAnalysisBudget();

        var shared = 0;
        while (budget.TryTake())
        {
            shared++;
        }

        Assert.Equal(32, shared);

        var review = 0;
        while (budget.TryTake(reviewPriority: true))
        {
            review++;
        }

        Assert.Equal(32, review);
    }

    [Fact]
    public void AudioAnalysisBudget_ZeroBudgetGrantsNothing()
    {
        var budget = new AudioAnalysisBudget(0);

        Assert.False(budget.TryTake());
        Assert.False(budget.TryTake(reviewPriority: true));
    }

    [Fact]
    public void AudioAnalysisBudget_ClampsConfiguredComparisons()
    {
        var budget = new AudioAnalysisBudget(1000, 0);

        var taken = 0;
        while (budget.TryTake())
        {
            taken++;
        }

        Assert.Equal(64, taken);
    }

    // ── UniqueSinglesHistoryRecorder ─────────────────────────────────

    [Fact]
    public void HistoryRecorder_WritesAttributedUnmonitorRow()
    {
        using var loggerHarness = new LoggerHarness();
        var repository = HistoryRepositoryProxy.Create();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), loggerHarness.Logger);
        var artist = new Artist { Id = 42, Name = "Test Artist" };
        var single = new Album { Id = 200, Title = "Single", AlbumType = "Single" };

        recorder.RecordUnmonitor(artist, single, "Covered by retained album");

        var row = Assert.Single(repository.Inserted);
        Assert.Equal(EntityHistoryEventType.Unknown, row.EventType);
        Assert.Equal(42, row.ArtistId);
        Assert.Equal(200, row.AlbumId);
        Assert.Equal("UniqueSingles", row.SourceTitle);
        Assert.Equal("UniqueSingles", row.Data["Plugin"]);
        Assert.Equal("Unmonitor", row.Data["Action"]);
        Assert.Equal("Covered by retained album", row.Data["Reason"]);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("action=unmonitor-recorded", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryRecorder_InsertFailureIsContained()
    {
        using var loggerHarness = new LoggerHarness();
        var repository = HistoryRepositoryProxy.Create();
        repository.ThrowOnInsert = true;
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), loggerHarness.Logger);

        var exception = Record.Exception(() => recorder.RecordUnmonitor(
            new Artist { Id = 42, Name = "Test Artist" },
            new Album { Id = 200, Title = "Single", AlbumType = "Single" },
            "reason"));

        Assert.Null(exception);
        Assert.Contains(loggerHarness.Entries, entry => entry.Contains("unmonitor-record-failed", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryRecorder_NullArgumentsAreIgnored()
    {
        using var loggerHarness = new LoggerHarness();
        var repository = HistoryRepositoryProxy.Create();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), loggerHarness.Logger);

        recorder.RecordUnmonitor(null!, new Album { Id = 1 }, "reason");
        recorder.RecordUnmonitor(new Artist { Id = 1 }, null!, "reason");

        Assert.Empty(repository.Inserted);
    }

    public class HistoryRepositoryProxy : DispatchProxy
    {
        public List<EntityHistory> Inserted { get; } = new();

        public bool ThrowOnInsert { get; set; }

        public IHistoryRepository AsRepository() => (IHistoryRepository)(object)this;

        public static HistoryRepositoryProxy Create()
        {
            var proxy = Create<IHistoryRepository, HistoryRepositoryProxy>();
            return (HistoryRepositoryProxy)(object)proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "Insert")
            {
                if (ThrowOnInsert)
                {
                    throw new InvalidOperationException("history insert failed");
                }

                var history = (EntityHistory)args![0]!;
                Inserted.Add(history);
                return history;
            }

            throw new NotSupportedException(method.Name);
        }
    }

    private sealed class LoggerHarness : IDisposable
    {
        private readonly LogFactory _factory;
        private readonly MemoryTarget _target;

        public LoggerHarness()
        {
            _factory = new LogFactory();
            _target = new MemoryTarget
            {
                Layout = "${level}|${message}|${exception:format=Type,Message}",
            };

            var config = new LoggingConfiguration();
            config.AddRuleForAllLevels(_target);
            _factory.Configuration = config;
            Logger = _factory.GetLogger(Guid.NewGuid().ToString("N"));
        }

        public Logger Logger { get; }

        public IList<string> Entries
        {
            get
            {
                _factory.Flush();
                return _target.Logs;
            }
        }

        public void Dispose()
        {
            _factory.Flush();
            _factory.Dispose();
        }
    }
}
