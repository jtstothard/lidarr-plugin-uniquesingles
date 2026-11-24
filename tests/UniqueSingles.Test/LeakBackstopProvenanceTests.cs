using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// The leak backstop only cleans files that raced the plugin's unmonitor: every file must trace
/// (TrackFileImported → Grabbed by DownloadId) to an automatic grab recorded at or before the
/// plugin's latest Unmonitor row. A user's later fetch (interactive or manual search, a grab
/// after the unmonitor, a manual import) is kept, as is anything whose origin is unknown.
/// History rows are built in the form Lidarr reads them back from the database: Data keys are
/// camelCased by its JSON dictionary-key policy.
/// </summary>
public partial class SingleCleanupServiceTests
{
    private static readonly DateTime UnmonitoredAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── End to end: SingleCleanupService + UniqueSinglesHistoryRecorder ──────

    [Theory]
    [InlineData(-60, "Rss", true)]                    // race: RSS grab before the unmonitor
    [InlineData(-60, "Search", true)]                 // race: automatic search before the unmonitor
    [InlineData(-60, null, true)]                     // grab row without a ReleaseSource key
    [InlineData(0, "Rss", true)]                      // contemporaneous
    [InlineData(3, "Rss", true)]                      // within the grab/unmonitor tolerance
    [InlineData(2880, "Rss", false)]                  // automatic grab two days after the unmonitor
    [InlineData(2880, "Search", false)]
    [InlineData(2880, "InteractiveSearch", false)]    // user grab from interactive search
    [InlineData(2880, "UserInvokedSearch", false)]    // user clicked Search on the single
    [InlineData(-60, "InteractiveSearch", false)]     // user grab, even before the unmonitor
    [InlineData(-60, "UserInvokedSearch", false)]
    public void LeakBackstop_CleansOnlyFilesFromAGrabThatRacedTheUnmonitor(int grabMinutesAfterUnmonitor, string? releaseSource, bool expectCleaned)
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        repository.Rows.Add(GrabRow(2, 200, UnmonitoredAt.AddMinutes(grabMinutesAfterUnmonitor), "dl-1", releaseSource));
        repository.Rows.Add(ImportRow(3, 200, UnmonitoredAt.AddMinutes(grabMinutesAfterUnmonitor + 30), fileId: 21, downloadId: "dl-1"));
        var (single, deleteMediaFiles, logger, service) = BackstopScenario(repository);

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        if (expectCleaned)
        {
            Assert.Equal(1, result.Cleaned);
            Assert.Single(deleteMediaFiles.DeletedFiles);
            Assert.Contains(logger.InfoMessages, m => m.Contains("reason=plugin-unmonitored-has-files", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(0, result.Cleaned);
            Assert.Equal(1, result.Skipped);
            Assert.Empty(deleteMediaFiles.DeletedFiles);
            Assert.Contains(logger.InfoMessages, m => m.Contains("reason=leak-backstop-not-race", StringComparison.Ordinal));
        }

        Assert.False(single.Monitored);
    }

    [Fact]
    public void LeakBackstop_ManualImportWithoutDownload_KeepsFiles()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        // An earlier automatic grab exists, but the files on disk came from a manual import.
        repository.Rows.Add(GrabRow(2, 200, UnmonitoredAt.AddMinutes(-60), "dl-1", "Rss"));
        repository.Rows.Add(ImportRow(3, 200, UnmonitoredAt.AddDays(1), fileId: 21, downloadId: null));
        var (single, deleteMediaFiles, logger, service) = BackstopScenario(repository);

        var result = service.ScanArtistWithOptions(Artist(), AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("imported-without-download", StringComparison.Ordinal));
    }

    [Fact]
    public void LeakBackstop_FilesWithoutImportHistory_KeepsFiles()
    {
        // Disk-scan imports (files placed in the artist folder) leave no TrackFileImported row.
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        repository.Rows.Add(GrabRow(2, 200, UnmonitoredAt.AddMinutes(-60), "dl-1", "Rss"));
        var (single, deleteMediaFiles, _, service) = BackstopScenario(repository);

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void LeakBackstop_ImportWithoutMatchingGrabRow_KeepsFiles()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        repository.Rows.Add(GrabRow(2, 200, UnmonitoredAt.AddMinutes(-60), "dl-other", "Rss"));
        repository.Rows.Add(ImportRow(3, 200, UnmonitoredAt.AddMinutes(30), fileId: 21, downloadId: "dl-1"));
        var (single, deleteMediaFiles, _, service) = BackstopScenario(repository);

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void LeakBackstop_OneFileFromAUserGrab_KeepsTheWholeRelease()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        repository.Rows.Add(GrabRow(2, 200, UnmonitoredAt.AddMinutes(-60), "dl-race", "Rss"));
        repository.Rows.Add(ImportRow(3, 200, UnmonitoredAt.AddMinutes(10), fileId: 21, downloadId: "dl-race"));
        repository.Rows.Add(GrabRow(4, 200, UnmonitoredAt.AddDays(2), "dl-user", "InteractiveSearch"));
        repository.Rows.Add(ImportRow(5, 200, UnmonitoredAt.AddDays(2).AddMinutes(10), fileId: 22, downloadId: "dl-user"));
        var (single, deleteMediaFiles, _, service) = BackstopScenario(repository, twoTracks: true);

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    // ── Recheck immediately before deletion ─────────────────────────────

    [Fact]
    public void LeakBackstop_UserRemonitorsDuringEvaluation_DeleteAborts()
    {
        var (single, albumService, deleteMediaFiles, history, logger, service) = RecheckScenario();
        // The user's re-monitor lands after the backstop's first provenance read, while the
        // release is being matched/analysed.
        history.OnGrabCheck = call =>
        {
            if (call == 1)
            {
                history.Provenance[single.Id] = UnmonitorProvenance.UserRemonitored;
            }
        };

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=backstop-state-changed", StringComparison.Ordinal)
            && m.Contains("provenance=UserRemonitored", StringComparison.Ordinal));
    }

    [Fact]
    public void LeakBackstop_ReleaseMonitoredAgainDuringEvaluation_DeleteAborts()
    {
        // A re-monitor whose UserRemonitor row could not be written still stops the delete.
        var (single, _, deleteMediaFiles, history, logger, service) = RecheckScenario();
        history.OnGrabCheck = call =>
        {
            if (call == 1)
            {
                single.Monitored = true;
            }
        };

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Contains(logger.InfoMessages, m => m.Contains("release-monitored-again", StringComparison.Ordinal));
    }

    [Fact]
    public void LeakBackstop_UserFetchReplacesFilesDuringEvaluation_DeleteAborts()
    {
        var (single, _, deleteMediaFiles, history, _, service) = RecheckScenario();
        history.OnGrabCheck = call =>
        {
            if (call == 1)
            {
                history.PreUnmonitorGrabAlbumIds.Remove(single.Id);
            }
        };

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Equal(2, history.GrabChecks.Count);
    }

    [Fact]
    public void LeakBackstop_NothingChangesDuringEvaluation_RecheckPassesAndDeletes()
    {
        var (single, _, deleteMediaFiles, history, _, service) = RecheckScenario();

        var result = service.CleanupSingleSelfCheckWithOptions(Artist(), single, AlbumOptions());

        Assert.Equal(1, result.Cleaned);
        Assert.Single(deleteMediaFiles.DeletedFiles);
        Assert.Equal(2, history.GrabChecks.Count);
    }

    // ── File-less unmonitor writes its History row first ─────────────────

    [Fact]
    public void FilelessUnmonitor_HistoryWriteFails_SingleStaysMonitored()
    {
        // Without the row a download landing later in the unmonitored release could never be
        // cleaned, so the unmonitor is skipped (the next scan retries).
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181500, "single-recording", fileId: 0));
        var logger = new RecordingLogger();
        var history = new RecordingHistoryRecorder { FailRecord = true };
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), logger, history);

        var result = service.ScanArtistWithOptions(Artist(), AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.UnmonitorFailures);
        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Contains(logger.WarnMessages, m => m.Contains("reason=unmonitor-history-write-failed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FilelessTier1Unmonitor_HistoryRecorderThrows_SingleStaysMonitored(bool legacyPath)
    {
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 0));
        var history = new RecordingHistoryRecorder { ThrowOnRecord = true };
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), historyRecorder: history);

        if (legacyPath)
        {
            service.CleanupSinglesForArtist(Artist(), album);
        }
        else
        {
            service.ScanArtistWithOptions(Artist(), AlbumOptions(eager: false));
        }

        Assert.True(single.Monitored);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void FilelessUnmonitor_WritesHistoryRowBeforeUnmonitoring()
    {
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181500, "single-recording", fileId: 0));
        var sequence = new List<string>();
        var history = new RecordingHistoryRecorder { Sequence = sequence };
        albumService.Sequence = sequence;
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), historyRecorder: history);

        var result = service.ScanArtistWithOptions(Artist(), AlbumOptions());

        Assert.Equal(1, result.Cleaned);
        Assert.Equal(new[] { $"history:unmonitor:{single.Id}", $"set-monitored:{single.Id}:False" }, sequence);
        Assert.False(single.Monitored);
    }

    [Fact]
    public void FilelessUnmonitor_UnmonitorFailsAfterRowWritten_RowIsReverted()
    {
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Single", "Single");
        var albumService = new RecordingAlbumService(album, single) { ThrowOnSetAlbumMonitored = true };
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181500, "single-recording", fileId: 0));
        var history = new RecordingHistoryRecorder();
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), historyRecorder: history);

        var result = service.ScanArtistWithOptions(Artist(), AlbumOptions());

        Assert.Equal(1, result.UnmonitorFailures);
        Assert.True(single.Monitored);
        Assert.Equal(new[] { single.Id }, history.Reverted);
        Assert.Empty(history.Unmonitors);
    }

    // ── UniqueSinglesHistoryRecorder ─────────────────────────────────────

    [Fact]
    public void HistoryRecorder_ReadsProvenanceFromDatabaseCamelCasedKeys()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        using var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);

        Assert.Equal(UnmonitorProvenance.PluginUnmonitored, recorder.GetProvenance(200));
    }

    [Fact]
    public void HistoryRecorder_UnmonitorReverted_LeavesNoPluginProvenance()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        repository.Rows.Add(PluginRow(2, 200, UnmonitoredAt.AddSeconds(1), "UnmonitorReverted"));
        using var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);

        Assert.Equal(UnmonitorProvenance.None, recorder.GetProvenance(200));
    }

    [Fact]
    public void HistoryRecorder_RecordUnmonitor_ReportsInsertFailure()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.ThrowOnInsert = true;
        using var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);

        Assert.False(recorder.RecordUnmonitor(Artist(), Album(200, "Single", "Single"), "reason"));
    }

    [Fact]
    public void HistoryRecorder_RecordUserRemonitor_WithoutArtist_StillProtectsTheRelease()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        using var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);

        Assert.True(recorder.RecordUserRemonitor(null, Album(200, "Single", "Single")));
        Assert.Equal(UnmonitorProvenance.UserRemonitored, recorder.GetProvenance(200));
    }

    [Fact]
    public void HistoryRecorder_GrabProvenanceLookupFailure_KeepsFiles()
    {
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.ThrowOnQuery = true;
        using var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);

        Assert.False(recorder.FilesCameFromPreUnmonitorGrab(200, new[] { 21 }, out var detail));
        Assert.Equal("lookup-failed", detail);
    }

    [Fact]
    public void HistoryRecorder_GrabProvenance_UsesLatestPluginUnmonitor()
    {
        // Unmonitored, re-monitored by the user, then unmonitored again: a grab between the
        // first unmonitor and the re-monitor predates the latest unmonitor and counts as racing it.
        var repository = ExactAudioFollowupsTests.HistoryRepositoryProxy.Create();
        repository.Rows.Add(PluginRow(1, 200, UnmonitoredAt, "Unmonitor"));
        repository.Rows.Add(GrabRow(2, 200, UnmonitoredAt.AddDays(1), "dl-1", "Rss"));
        repository.Rows.Add(ImportRow(3, 200, UnmonitoredAt.AddDays(4), fileId: 21, downloadId: "dl-1"));
        repository.Rows.Add(PluginRow(4, 200, UnmonitoredAt.AddDays(3), "Unmonitor"));
        using var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);

        Assert.True(recorder.FilesCameFromPreUnmonitorGrab(200, new[] { 21 }, out _));
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static (Album Single, RecordingDeleteMediaFiles Delete, RecordingLogger Logger, SingleCleanupService Service) BackstopScenario(
        ExactAudioFollowupsTests.HistoryRepositoryProxy repository,
        bool twoTracks = false)
    {
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Leaked Single", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService();
        var mediaFileService = new RecordingMediaFileService();
        if (twoTracks)
        {
            trackService
                .WithTracks(album.Id, Track("Song", 180000, "mbid-1", fileId: 11), Track("Other", 200000, "mbid-2", fileId: 12))
                .WithTracks(single.Id, Track("Song", 180000, "mbid-1", fileId: 21), Track("Other", 200000, "mbid-2", fileId: 22));
            mediaFileService.WithFiles(single.Id, File(21, single.Id, "/music/a.flac"), File(22, single.Id, "/music/b.flac"));
        }
        else
        {
            trackService
                .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
                .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 21));
            mediaFileService.WithFiles(single.Id, File(21, single.Id, "/music/leaked-single.flac"));
        }

        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var recorder = new UniqueSinglesHistoryRecorder(repository.AsRepository(), logger.Logger);
        var service = new SingleCleanupService(albumService, trackService, mediaFileService, deleteMediaFiles, recorder, new StubInFlightProbe(), new RecordingFolderCleanup(), logger.Logger);
        return (single, deleteMediaFiles, logger, service);
    }

    private static (Album Single, RecordingAlbumService AlbumService, RecordingDeleteMediaFiles Delete, RecordingHistoryRecorder History, RecordingLogger Logger, SingleCleanupService Service) RecheckScenario()
    {
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Leaked Single", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/music/leaked-single.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = UnmonitorProvenance.PluginUnmonitored;
        history.PreUnmonitorGrabAlbumIds.Add(single.Id);
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger, history);
        return (single, albumService, deleteMediaFiles, history, logger, service);
    }

    // Lidarr's EmbeddedDocumentConverter serializes History Data with a camelCase
    // dictionary-key policy, so rows read back from the database carry camelCased keys.
    private static EntityHistory PluginRow(int id, int albumId, DateTime date, string action)
    {
        var row = new EntityHistory { Id = id, AlbumId = albumId, Date = date, EventType = EntityHistoryEventType.Unknown };
        row.Data["plugin"] = "UniqueSingles";
        row.Data["action"] = action;
        row.Data["reason"] = "test";
        return row;
    }

    private static EntityHistory GrabRow(int id, int albumId, DateTime date, string downloadId, string? releaseSource)
    {
        var row = new EntityHistory { Id = id, AlbumId = albumId, Date = date, EventType = EntityHistoryEventType.Grabbed, DownloadId = downloadId };
        if (releaseSource != null)
        {
            row.Data["releaseSource"] = releaseSource;
        }

        return row;
    }

    private static EntityHistory ImportRow(int id, int albumId, DateTime date, int fileId, string? downloadId)
    {
        var row = new EntityHistory { Id = id, AlbumId = albumId, Date = date, EventType = EntityHistoryEventType.TrackFileImported, DownloadId = downloadId! };
        row.Data["fileId"] = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return row;
    }
}
