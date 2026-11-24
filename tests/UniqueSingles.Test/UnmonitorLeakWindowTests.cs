using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Leak window in the file-less unmonitor paths: a redundant single must never end up holding
/// downloaded files that are neither monitored nor cleaned.
/// (a) A single with a download in flight is not unmonitored while file-less.
/// (b) Files that land in a release the plugin unmonitored as redundant are cleaned under the
///     normal cleanup rules instead of being skipped.
/// (c) Releases unmonitored by the user (no plugin History row) are never touched.
/// (d) Releases the user re-monitored after the plugin unmonitored them are never touched.
/// </summary>
public partial class SingleCleanupServiceTests
{
    // ── (a) in-flight downloads keep file-less singles monitored ─────────────

    [Fact]
    public void ScanArtistWithOptions_Tier2RedundantNoFiles_DownloadInFlight_StaysMonitored()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Downloading Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181500, "single-recording", fileId: 0));
        var logger = new RecordingLogger();
        var history = new RecordingHistoryRecorder();
        var probe = new StubInFlightProbe();
        probe.InFlightAlbumIds.Add(single.Id);
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), logger, history, probe);

        var result = service.ScanArtistWithOptions(artist, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(history.Unmonitors);
        Assert.True(single.Monitored);
        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=download-in-flight", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanArtistWithOptions_Tier1RedundantNoFiles_DownloadInFlight_StaysMonitored()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Downloading Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 0));
        var probe = new StubInFlightProbe();
        probe.InFlightAlbumIds.Add(single.Id);
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), inFlightProbe: probe);

        // Tier-1 file-less unmonitor runs regardless of the eager setting.
        var result = service.ScanArtistWithOptions(artist, AlbumOptions(eager: false));

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.True(single.Monitored);
    }

    [Fact]
    public void CleanupWithOptions_AlbumImport_Tier2RedundantNoFilesSingle_DownloadInFlight_StaysMonitored()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Downloading Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181500, "single-recording", fileId: 0));
        var probe = new StubInFlightProbe();
        probe.InFlightAlbumIds.Add(single.Id);
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), inFlightProbe: probe);

        var result = service.CleanupWithOptions(artist, album, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Empty(albumService.SetMonitoredCalls);
    }

    [Fact]
    public void LegacyCleanupSinglesForArtist_Tier1RedundantNoFiles_DownloadInFlight_StaysMonitored()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Downloading Single", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 0));
        var probe = new StubInFlightProbe();
        probe.InFlightAlbumIds.Add(single.Id);
        var service = Service(albumService, trackService, new RecordingMediaFileService(), new RecordingDeleteMediaFiles(), inFlightProbe: probe);

        service.CleanupSinglesForArtist(artist, album);

        Assert.Empty(albumService.SetMonitoredCalls);
    }

    // ── (b) files landing in a plugin-unmonitored release are cleaned ────────

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_PluginUnmonitoredSingleReceivesRedundantFiles_DeletesThem()
    {
        // The grab landed before the unmonitor; the download then imported into the
        // plugin-unmonitored release. The import self-check must clean it, not skip it.
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Leaked Single", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 21));
        var singleFile = File(21, single.Id, "/music/leaked-single.flac");
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, singleFile);
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = UnmonitorProvenance.PluginUnmonitored;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger, history);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, single, AlbumOptions());

        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.DeleteFailures);
        Assert.Equal(new[] { singleFile }, deleteMediaFiles.DeletedFiles.Select(d => d.TrackFile));
        // Already unmonitored with an audit row: no second unmonitor or audit row.
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(history.Unmonitors);
        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=plugin-unmonitored-has-files", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanArtistWithOptions_PluginUnmonitoredSingleWithRedundantFiles_DeletesThem()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Leaked Single", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 181000, "single-recording", fileId: 21)); // Tier-2
        var singleFile = File(21, single.Id, "/music/leaked-single.flac");
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, singleFile);
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = UnmonitorProvenance.PluginUnmonitored;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, historyRecorder: history);

        var result = service.ScanArtistWithOptions(artist, AlbumOptions());

        Assert.Equal(1, result.Cleaned);
        Assert.Equal(new[] { singleFile }, deleteMediaFiles.DeletedFiles.Select(d => d.TrackFile));
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_PluginUnmonitoredSingleWithTitleOnlyFiles_FlagOnly_FlagsReviewWithoutDeleting()
    {
        // The backstop grants no extra authority: Tier-3 title-only evidence under FlagOnly is
        // flagged for review, exactly as for a monitored single.
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Radio Edit", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 240000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "single-recording", fileId: 21));
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/music/radio-edit.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = UnmonitorProvenance.PluginUnmonitored;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, historyRecorder: history);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.ReviewNeeded);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_PluginUnmonitoredSingleWithTitleOnlyFiles_AutoClean_DeletesThem()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Radio Edit", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 240000, "album-recording", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "single-recording", fileId: 21));
        var singleFile = File(21, single.Id, "/music/radio-edit.flac");
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, singleFile);
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = UnmonitorProvenance.PluginUnmonitored;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, historyRecorder: history);
        var options = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" }, Tier3Action.AutoClean);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, single, options);

        Assert.Equal(1, result.Cleaned);
        Assert.Equal(new[] { singleFile }, deleteMediaFiles.DeletedFiles.Select(d => d.TrackFile));
    }

    // ── (c) user-unmonitored releases are never touched ──────────────────────

    [Theory]
    [InlineData(UnmonitorProvenance.None)]            // never unmonitored by the plugin
    [InlineData(UnmonitorProvenance.UserRemonitored)] // re-monitored, then unmonitored by hand
    [InlineData(UnmonitorProvenance.Unknown)]         // provenance unreadable
    public void ScanAndSelfCheck_UnmonitoredWithoutPluginProvenance_NeverTouched(UnmonitorProvenance provenance)
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Kept By User", "Single", monitored: false);
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/music/kept.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = provenance;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, historyRecorder: history);
        var autoClean = new SingleCleanupOptions(3000, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Album" }, Tier3Action.AutoClean);

        var scan = service.ScanArtistWithOptions(artist, autoClean);
        var selfCheck = service.CleanupSingleSelfCheckWithOptions(artist, single, autoClean);

        Assert.Equal(0, scan.Cleaned + selfCheck.Cleaned);
        Assert.Equal(2, scan.Skipped + selfCheck.Skipped);
        Assert.DoesNotContain(single.Id, trackService.AlbumLookupIds);
        Assert.Empty(mediaFileService.AlbumLookupIds);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
    }

    // ── (d) user re-monitored releases are never touched ─────────────────────

    [Fact]
    public void CleanupSingleSelfCheckWithOptions_UserRemonitoredSingleWithRedundantFiles_NeverDeleted()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var single = Album(200, "Wanted Again", "Single");
        var albumService = new RecordingAlbumService(album, single);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id, Track("Song", 180000, "shared-mbid", fileId: 11))
            .WithTracks(single.Id, Track("Song", 180000, "shared-mbid", fileId: 21));
        var mediaFileService = new RecordingMediaFileService().WithFiles(single.Id, File(21, single.Id, "/music/wanted.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var logger = new RecordingLogger();
        var history = new RecordingHistoryRecorder();
        history.Provenance[single.Id] = UnmonitorProvenance.UserRemonitored;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, logger, history);

        var result = service.CleanupSingleSelfCheckWithOptions(artist, single, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.True(single.Monitored);
        Assert.Contains(logger.InfoMessages, m => m.Contains("reason=user-remonitored", StringComparison.Ordinal));
    }

    [Fact]
    public void ScanArtistWithOptions_UserRemonitoredSingles_NeverUnmonitoredOrDeleted()
    {
        var artist = Artist();
        var album = Album(100, "Album", "Album");
        var withFiles = Album(200, "Wanted Again", "Single");
        var fileless = Album(201, "Also Wanted Again", "Single");
        var albumService = new RecordingAlbumService(album, withFiles, fileless);
        var trackService = new RecordingTrackService()
            .WithTracks(album.Id,
                Track("Song", 180000, "shared-mbid", fileId: 11),
                Track("Other", 200000, "other-mbid", fileId: 12))
            .WithTracks(withFiles.Id, Track("Song", 180000, "shared-mbid", fileId: 21))
            .WithTracks(fileless.Id, Track("Other", 200000, "other-mbid", fileId: 0));
        var mediaFileService = new RecordingMediaFileService().WithFiles(withFiles.Id, File(21, withFiles.Id, "/music/wanted.flac"));
        var deleteMediaFiles = new RecordingDeleteMediaFiles();
        var history = new RecordingHistoryRecorder();
        history.Provenance[withFiles.Id] = UnmonitorProvenance.UserRemonitored;
        history.Provenance[fileless.Id] = UnmonitorProvenance.UserRemonitored;
        var service = Service(albumService, trackService, mediaFileService, deleteMediaFiles, historyRecorder: history);

        var result = service.ScanArtistWithOptions(artist, AlbumOptions());

        Assert.Equal(0, result.Cleaned);
        Assert.Equal(2, result.Skipped);
        Assert.Empty(albumService.SetMonitoredCalls);
        Assert.Empty(deleteMediaFiles.DeletedFiles);
        Assert.Empty(history.Unmonitors);
    }
}
