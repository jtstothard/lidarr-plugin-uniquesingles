using NzbDrone.Core.Plugins;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using Xunit;

namespace UniqueSingles.Test;

public partial class SingleCleanupServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactAudio_ScanAndImport_ReportCompleteDuplicateWithoutCleanup(int route)
    {
        var album = Album(100, "Retained", "Album");
        var single = Album(200, "Candidate", "Single");
        var albums = new RecordingAlbumService(album, single);
        var source = Track("Song", 900000, "wrong-source-id", 11);
        source.Id = 1;
        source.AlbumId = 100;
        var candidate = Track("Song", 100000, "wrong-candidate-id", 21);
        candidate.Id = 2;
        candidate.AlbumId = 200;
        var tracks = new RecordingTrackService().WithTracks(100, source).WithTracks(200, candidate);
        var files = new RecordingMediaFileService()
            .WithFiles(100, File(11, 100, AudioFixture("same-tags.flac")))
            .WithFiles(200, File(21, 200, AudioFixture("original.flac")));
        var deletes = new RecordingDeleteMediaFiles();
        using var log = new RecordingLogger();
        var service = Service(albums, tracks, files, deletes, log);
        var options = new UniqueSinglesSettings { ExactAudioAnalysis = true }.ToCleanupOptions();

        var result = route == 0 ? service.ScanArtistWithOptions(Artist(), options)
            : new UniqueSinglesImportCoordinator(service, log.Logger).HandleImport(
                new AlbumDownloadMessage { Artist = Artist(), Album = route == 1 ? single : album }, options);

        Assert.True(result.ExactAudioReports == 1, string.Join("\n", log.InfoMessages));
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Empty(albums.SetMonitoredCalls);
        Assert.Empty(deletes.DeletedFiles);
        Assert.True(single.Monitored);
        Assert.Contains(log.InfoMessages, m => m.Contains("report-only-exact-duplicate"));
        Assert.Contains(log.InfoMessages, m => m.Contains("sourceTrackId=1") && m.Contains("candidateTrackId=2"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactCleanup_ExplicitOptInCleansAndRetainsSource(int route)
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        var settings = new UniqueSinglesSettings();
        Assert.False(settings.ToCleanupOptions().ExactAudioCleanup);
        settings.ExactAudioCleanup = true;
        var result = AudioRun(h, route, settings.ToCleanupOptions());
        Assert.Equal(1, result.Cleaned);
        Assert.False(h.Single.Monitored);
        Assert.True(h.Source.Monitored);
        Assert.Single(h.Deletes.DeletedFiles);
    }

    [Fact]
    public void ExactCleanup_SharedFilePathCannotBeItsOwnSurvivor()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Files.WithFiles(100, File(11, 100, AudioFixture("original.flac")));
        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());
        Assert.Equal(0, result.Cleaned);
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
    }

    [Theory]
    [InlineData("source-missing")]
    [InlineData("source-replaced")]
    [InlineData("candidate-mutated")]
    [InlineData("unmonitored")]
    [InlineData("excluded")]
    [InlineData("type")]
    [InlineData("track-missing")]
    public void ExactCleanup_RevalidatesAfterVerificationOnEveryRoute(string change)
    {
        for (var route = 0; route < 3; route++)
        {
            var h = AudioHarness("same-tags.flac");
            using var log = h.Log;
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                var candidate = System.IO.Path.Combine(directory, "candidate.flac");
                var source = System.IO.Path.Combine(directory, "source.flac");
                System.IO.File.Copy(AudioFixture("original.flac"), candidate);
                System.IO.File.Copy(AudioFixture("same-tags.flac"), source);
                h.Files.WithFiles(100, File(11, 100, source)).WithFiles(200, File(21, 200, candidate));
                var lookups = 0;
                h.Albums.BeforeArtistLookup = () =>
                {
                    if (++lookups != 2) return;
                    if (change == "source-missing") System.IO.File.Delete(source);
                    if (change == "source-replaced") System.IO.File.Copy(AudioFixture("late-sample.flac"), source, true);
                    if (change == "candidate-mutated") System.IO.File.Copy(AudioFixture("late-sample.flac"), candidate, true);
                    if (change == "unmonitored") h.Source.Monitored = false;
                    if (change == "excluded") h.Source.SecondaryTypes = new() { SecondaryAlbumType.Live };
                    if (change == "type") h.Source.AlbumType = "Single";
                    if (change == "track-missing") h.SourceTrack.TrackFileId = 0;
                };
                var result = AudioRun(h, route, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());
                Assert.Equal(0, result.Cleaned);
                Assert.True(h.Single.Monitored);
                Assert.Empty(h.Albums.SetMonitoredCalls);
                Assert.Empty(h.Deletes.DeletedFiles);
                Assert.Contains(log.InfoMessages, m => m.Contains("VerifiedExact"));
            }
            finally { System.IO.Directory.Delete(directory, true); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCleanup_PreservesFailureHandling(bool deleteFailure)
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Albums.ThrowOnSetAlbumMonitored = !deleteFailure;
        h.Deletes.ThrowOnDelete = deleteFailure;
        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());
        Assert.Equal(deleteFailure ? 1 : 0, result.DeleteFailures);
        Assert.Equal(deleteFailure ? 0 : 1, result.UnmonitorFailures);
        if (!deleteFailure) Assert.Empty(h.Deletes.DeletedFiles);
    }

    [Fact]
    public void ExactCleanup_SourceBecomesIneligibleAfterUnmonitor_PreventsDeletion()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Albums.AfterUnmonitor = () => h.Source.Monitored = false;
        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());
        Assert.Equal(1, result.DeleteFailures);
        Assert.Empty(h.Deletes.DeletedFiles);
        Assert.False(h.Single.Monitored);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactCleanup_CompleteMultitrackCoverageCleansWholeRelease(int route)
    {
        var retainedStereo = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".flac");
        System.IO.File.Copy(AudioFixture("stereo.flac"), retainedStereo);
        try
        {
            var h = AudioHarness("same-tags.flac");
            using var log = h.Log;
            var second = Track("Other", 100000, "other-candidate", 22);
            second.Id = 3;
            var source = Track("Other", 900000, "other-source", 12);
            source.Id = 4;
            h.Tracks.WithTracks(200, h.Candidate, second).WithTracks(100, h.SourceTrack, source);
            h.Files.WithFiles(200, File(21, 200, AudioFixture("original.flac")), File(22, 200, AudioFixture("stereo.flac")))
                .WithFiles(100, File(11, 100, AudioFixture("same-tags.flac")), File(12, 100, retainedStereo));
            var result = AudioRun(h, route, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());
            Assert.Equal(1, result.Cleaned);
            Assert.Equal(2, h.Deletes.DeletedFiles.Count);
            Assert.True(h.Source.Monitored);
        }
        finally { System.IO.File.Delete(retainedStereo); }
    }

    [Fact]
    public void ExactCleanup_EquivalentSinglesRetainOneSurvivor()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Source.AlbumType = "Single";
        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioCleanup = true, ReleaseTypesToCheck = "Single" }.ToCleanupOptions());
        Assert.Equal(1, result.Cleaned);
        Assert.Single(h.Albums.SetMonitoredCalls);
        Assert.Single(h.Deletes.DeletedFiles);
        Assert.True(h.Single.Monitored || h.Source.Monitored);
    }

    private static string AudioFixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "AudioFixtures", name);

    [Theory]
    [InlineData("late-sample.flac", "Different")]
    [InlineData("different-count.flac", "Different")]
    [InlineData("different-rate.flac", "Different")]
    [InlineData("different-depth.flac", "Different")]
    [InlineData("stereo.flac", "Different")]
    [InlineData("lying-rate.flac", "Unavailable")]
    [InlineData("forged-md5.flac", "Unavailable")]
    [InlineData("corrupt.flac", "Unavailable")]
    [InlineData("truncated.flac", "Unavailable")]
    [InlineData("missing.flac", "Unavailable")]
    [InlineData("original.mp3", "Unavailable")]
    public void ExactAudio_ScanAndBothImportRoutes_RejectUnsafeEvidence(string sourceFixture, string status)
    {
        for (var route = 0; route < 3; route++)
        {
            var harness = AudioHarness(sourceFixture);
            using var log = harness.Log;
            var result = AudioRun(harness, route, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());
            Assert.Equal(0, result.ExactAudioReports);
            Assert.Equal(1, result.ReviewNeeded);
            Assert.Equal(0, result.Cleaned);
            Assert.Empty(harness.Albums.SetMonitoredCalls);
            Assert.Empty(harness.Deletes.DeletedFiles);
            Assert.Contains(log.InfoMessages, m => m.Contains("status=" + status));
        }
    }

    [Theory]
    [InlineData("exclusive")]
    [InlineData("missing-track")]
    [InlineData("second-version")]
    [InlineData("unmonitored")]
    [InlineData("excluded")]
    [InlineData("type")]
    [InlineData("self")]
    public void ExactAudio_ScanAndImport_ProtectWholeReleaseAndSourcePolicy(string protection)
    {
        for (var route = 0; route < 3; route++)
        {
            var h = AudioHarness("same-tags.flac");
            using var log = h.Log;
            var options = new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions();
            if (protection == "exclusive") h.Tracks.WithTracks(200, h.Candidate, Track("Exclusive", 9000, "exclusive", 22));
            if (protection == "missing-track") h.Tracks.WithTracks(200, h.Candidate, Track("Song", 9000, "missing", 0));
            if (protection == "second-version")
            {
                var second = Track("Song", 100000, "second-version", 22);
                h.Tracks.WithTracks(200, h.Candidate, second);
                h.Files.WithFiles(200, File(21, 200, AudioFixture("original.flac")), File(22, 200, AudioFixture("late-sample.flac")));
            }
            if (protection == "unmonitored") h.Source.Monitored = false;
            if (protection == "excluded") h.Source.SecondaryTypes = new List<SecondaryAlbumType> { SecondaryAlbumType.Live };
            if (protection == "type") options = new SingleCleanupOptions(3000, new() { "EP" }, Tier3Action.FlagOnly, true);
            if (protection == "self")
            {
                h.Source.Monitored = false;
                options = new SingleCleanupOptions(3000, new() { "Single" }, Tier3Action.FlagOnly, true);
            }
            var result = AudioRun(h, route, options);
            Assert.Equal(0, result.ExactAudioReports);
            Assert.Empty(h.Albums.SetMonitoredCalls);
            Assert.Empty(h.Deletes.DeletedFiles);
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("absent-decoder")]
    [InlineData("cancelled")]
    [InlineData("budget")]
    public void ExactAudio_OptionalAnalysisCannotFabricateSuccess(string reason)
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        var options = new SingleCleanupOptions(3000, new() { "Album" }, Tier3Action.FlagOnly,
            exactAudioAnalysis: reason != "disabled",
            exactAudioDecoderPath: reason == "absent-decoder" ? "/no-such-decoder-issue39" : "ffmpeg",
            audioCancellation: new System.Threading.CancellationToken(reason == "cancelled"),
            audioBudget: new AudioAnalysisBudget(reason == "budget" ? 0 : 32));
        var result = AudioRun(h, 0, options);
        Assert.Equal(0, result.ExactAudioReports);
        Assert.Equal(1, result.ReviewNeeded);
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
        if (reason == "disabled") Assert.DoesNotContain(log.InfoMessages, m => m.Contains("exact-audio:"));
        else Assert.Contains(log.InfoMessages, m => m.Contains("status=Unavailable"));
    }

    [Fact]
    public void ExactAudio_CompleteMultitrackReportRequiresEveryCounterpart()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        var second = Track("Other", 100000, "other-candidate", 22);
        second.Id = 3;
        var source = Track("Other", 900000, "other-source", 12);
        source.Id = 4;
        h.Tracks.WithTracks(200, h.Candidate, second).WithTracks(100, h.SourceTrack, source);
        h.Files.WithFiles(200, File(21, 200, AudioFixture("original.flac")), File(22, 200, AudioFixture("stereo.flac")))
            .WithFiles(100, File(11, 100, AudioFixture("same-tags.flac")), File(12, 100, AudioFixture("stereo.flac")));
        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioAnalysis = true }.ToCleanupOptions());
        Assert.Equal(1, result.ExactAudioReports);
        Assert.Contains(log.InfoMessages, m => m.Contains("candidateTrackId=3") && m.Contains("sourceTrackId=4") && m.Contains("VerifiedExact"));
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
    }

    [Fact]
    public void ExactAudio_DefaultsOffAndMetadataCleanupStillWorksWhenEnabled()
    {
        Assert.False(new UniqueSinglesSettings().ToCleanupOptions().ExactAudioAnalysis);
        var h = AudioHarness("missing.flac");
        using var log = h.Log;
        h.Candidate.ForeignRecordingId = h.SourceTrack.ForeignRecordingId;
        var result = AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioAnalysis = true }.ToCleanupOptions());
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.ExactAudioReports);
        Assert.Single(h.Albums.SetMonitoredCalls);
        Assert.Single(h.Deletes.DeletedFiles);
        Assert.DoesNotContain(log.InfoMessages, m => m.Contains("exact-audio:"));
    }

    [Fact]
    public void ExactAudio_TwoEquivalentSinglesBothRetainTheirCounterpartsInReportMode()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Source.AlbumType = "Single";
        var options = new SingleCleanupOptions(3000, new() { "Single" }, Tier3Action.FlagOnly, true);
        var result = AudioRun(h, 0, options);
        Assert.Equal(2, result.ExactAudioReports);
        Assert.True(h.Source.Monitored);
        Assert.True(h.Single.Monitored);
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
    }

    private static AudioTestHarness AudioHarness(string sourceFixture)
    {
        var source = Album(100, "Retained", "Album");
        var single = Album(200, "Candidate", "Single");
        var candidate = Track("Song", 100000, "candidate", 21);
        candidate.Id = 2;
        var sourceTrack = Track("Song", 900000, "source", 11);
        sourceTrack.Id = 1;
        return new AudioTestHarness(new RecordingAlbumService(source, single),
            new RecordingTrackService().WithTracks(100, sourceTrack).WithTracks(200, candidate),
            new RecordingMediaFileService().WithFiles(100, File(11, 100, AudioFixture(sourceFixture)))
                .WithFiles(200, File(21, 200, AudioFixture("original.flac"))),
            new RecordingDeleteMediaFiles(), new RecordingLogger(), source, single, candidate, sourceTrack);
    }

    private static CleanupResult AudioRun(AudioTestHarness h, int route, SingleCleanupOptions options)
    {
        var service = Service(h.Albums, h.Tracks, h.Files, h.Deletes, h.Log);
        if (route == 0) return service.ScanArtistWithOptions(Artist(), options);
        return new UniqueSinglesImportCoordinator(service, h.Log.Logger).HandleImport(
            new AlbumDownloadMessage { Artist = Artist(), Album = route == 1 ? h.Single : h.Source }, options);
    }

    private sealed record AudioTestHarness(RecordingAlbumService Albums, RecordingTrackService Tracks,
        RecordingMediaFileService Files, RecordingDeleteMediaFiles Deletes, RecordingLogger Log,
        Album Source, Album Single, Track Candidate, Track SourceTrack);
}
