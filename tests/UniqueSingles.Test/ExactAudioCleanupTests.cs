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
    public void ExactAudio_ReportOnlyPreservesMetadataAutoCleanOnScanAndImport(int route)
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        var options = new UniqueSinglesSettings
        {
            ExactAudioAnalysis = true,
            Tier3Action = Tier3Action.AutoClean,
        }.ToCleanupOptions();

        var result = AudioRun(h, route, options);

        Assert.Contains(log.InfoMessages, m => m.Contains("report-only-exact-duplicate"));
        Assert.Equal(1, result.CandidatesChecked);
        Assert.Equal(1, result.Cleaned);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.ExactAudioReports);
        Assert.Equal(0, result.UnmonitorFailures);
        Assert.Equal(0, result.DeleteFailures);
        Assert.False(h.Single.Monitored);
        Assert.True(h.Source.Monitored);
        Assert.Single(h.Albums.SetMonitoredCalls);
        Assert.Equal(21, Assert.Single(h.Deletes.DeletedFiles).TrackFile.Id);
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactAudio_RepeatedAnalysisRevalidatesBothPathsAndPreservesFiles(int route)
    {
        var directory = Path.Combine(Path.GetTempPath(), "uniquesingles-cache-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var candidate = Path.Combine(directory, "candidate.flac");
            var source = Path.Combine(directory, "source.flac");
            System.IO.File.Copy(AudioFixture("original.flac"), candidate);
            System.IO.File.Copy(AudioFixture("same-tags.flac"), source);
            var h = AudioHarness("same-tags.flac");
            using var log = h.Log;
            h.Files.WithFiles(100, File(11, 100, source)).WithFiles(200, File(21, 200, candidate));
            SingleCleanupOptions Options() => new UniqueSinglesSettings { ExactAudioAnalysis = true }.ToCleanupOptions();
            Assert.Equal(1, AudioRun(h, route, Options()).ExactAudioReports);
            Assert.Equal(1, AudioRun(h, route, Options()).ExactAudioReports);
            System.IO.File.Copy(AudioFixture("late-sample.flac"), source, true);
            Assert.Equal(0, AudioRun(h, route, Options()).ExactAudioReports);
            System.IO.File.Copy(AudioFixture("same-tags.flac"), source, true);
            System.IO.File.Copy(AudioFixture("late-sample.flac"), candidate, true);
            Assert.Equal(0, AudioRun(h, route, Options()).ExactAudioReports);
            System.IO.File.Delete(candidate);
            Assert.Equal(0, AudioRun(h, route, Options()).ExactAudioReports);
            Assert.Empty(h.Albums.SetMonitoredCalls);
            Assert.Empty(h.Deletes.DeletedFiles);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactAudio_ReleaseLineReportsConfiguredCleanupAuthority(bool exactAudioCleanup)
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        var options = new SingleCleanupOptions(3000, new() { "Album" }, Tier3Action.FlagOnly,
            exactAudioAnalysis: true, exactAudioCleanup: exactAudioCleanup);

        var result = AudioRun(h, 0, options);

        var releaseLine = Assert.Single(log.InfoMessages, m =>
            m.Contains("exact-audio release:") && m.Contains("candidateReleaseId=200"));
        Assert.Contains(exactAudioCleanup ? "cleanupAuthority=exact-audio-cleanup" : "cleanupAuthority=none", releaseLine);
        // The tier3 candidate is review-needed, so the run must be labelled review-priority.
        Assert.Contains("reviewPriority=True", releaseLine);
        if (!exactAudioCleanup)
        {
            Assert.Equal(1, result.ExactAudioReports);
            Assert.True(h.Single.Monitored);
        }
    }

    [Fact]
    public void ExactAudio_NonReviewCandidateReleaseLineIsNotLabelledReviewPriority()
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        // A downloaded exclusive B-side makes the whole release a keep, not a review candidate.
        h.Tracks.WithTracks(200, h.Candidate, Track("Exclusive", 9000, "exclusive", 22));
        h.Files.WithFiles(200, File(21, 200, AudioFixture("original.flac")), File(22, 200, AudioFixture("late-sample.flac")));

        AudioRun(h, 0, new UniqueSinglesSettings { ExactAudioAnalysis = true }.ToCleanupOptions());

        var releaseLine = Assert.Single(log.InfoMessages, m =>
            m.Contains("exact-audio release:") && m.Contains("candidateReleaseId=200"));
        Assert.Contains("reviewPriority=False", releaseLine);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactAudio_ReviewNeededCandidatesReceiveComparisonAttempts(int route)
    {
        var h = ReviewPriorityHarness();
        using var log = h.Log;
        // One comparison total, reserved for review-priority candidates: the earlier
        // keep-exclusive candidates cannot consume it (production finding 2026-10-29).
        var options = new SingleCleanupOptions(3000, new() { "Album" }, Tier3Action.FlagOnly,
            exactAudioAnalysis: true, audioBudget: new AudioAnalysisBudget(1, 1));

        var result = ReviewPriorityRun(h, route, options);

        Assert.True(result.ExactAudioReports == 1, string.Join("\n", log.InfoMessages));
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(route == 1 ? 0 : 3, result.KeepExclusive);
        Assert.Equal(0, result.Cleaned);
        Assert.Contains(log.InfoMessages, m =>
            m.Contains("candidateTrackId=" + h.ReviewTrack.Id) && m.Contains("status=VerifiedExact"));
        Assert.DoesNotContain(log.InfoMessages, m =>
            m.Contains("candidateTrackId=" + h.ReviewTrack.Id) && m.Contains("comparison-budget-exhausted"));
        Assert.Contains(log.InfoMessages, m =>
            m.Contains("exact-audio release:") && m.Contains("candidateReleaseId=204") && m.Contains("decision=report-only-exact-duplicate"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactAudio_ReviewNeededCandidatesAreAnalyzedBeforeOtherCandidates(int route)
    {
        var h = ReviewPriorityHarness();
        using var log = h.Log;
        // No reserved lane at all: the earlier candidates can drain the whole budget, so the
        // review-needed release only receives an attempt when it is analyzed first.
        var options = new SingleCleanupOptions(3000, new() { "Album" }, Tier3Action.FlagOnly,
            exactAudioAnalysis: true, audioBudget: new AudioAnalysisBudget(2, 0));

        var result = ReviewPriorityRun(h, route, options);

        Assert.Equal(1, result.ExactAudioReports);
        Assert.Equal(0, result.ReviewNeeded);
        var audioLines = log.InfoMessages.Where(m => m.Contains("UniqueSingles exact-audio:") && m.Contains("status=")).ToList();
        Assert.True(audioLines.Count > 0, string.Join("\n", log.InfoMessages));
        Assert.Contains("candidateReleaseId=204", audioLines[0]);
        if (route != 1)
        {
            Assert.Contains(log.InfoMessages, m => m.Contains("reason=review-priority-first"));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactAudio_KeepExclusiveOutcomesCountSeparatelyFromSkipped(int route)
    {
        var h = AudioHarness("same-tags.flac");
        using var log = h.Log;
        h.Tracks.WithTracks(200, h.Candidate, Track("Exclusive", 9000, "exclusive", 22));
        h.Files.WithFiles(200, File(21, 200, AudioFixture("original.flac")), File(22, 200, AudioFixture("late-sample.flac")));
        var options = new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions();

        var result = AudioRun(h, route, options);

        // Incomplete audio coverage (different PCM) falls back to the whole-release metadata
        // decision, whose keep-exclusive outcome is now its own summary aggregate.
        Assert.Equal(1, result.KeepExclusive);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.ReviewNeeded);
        Assert.Equal(0, result.Cleaned);
        Assert.Equal(1, log.InfoMessages.Count(m => m.Contains("outcome=keep-exclusive") && m.Contains("singleId=200")));
        Assert.Empty(h.Albums.SetMonitoredCalls);
        Assert.Empty(h.Deletes.DeletedFiles);
    }

    private static string AudioFixture(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "AudioFixtures", name);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactCleanup_CandidateMutatedInsideDeleteCallIsDetectedAndStopsRemainingDeletions(int route)
    {
        if (OperatingSystem.IsWindows()) return; // POSIX delete-guard proof; Windows share enforcement blocks the simulated in-place writer itself.
        var (h, directory, first, second, sourceMain, sourceSecond) = GuardedDeleteHarness();
        using var log = h.Log;
        try
        {
            // External in-place mutation lands between the final per-file revalidation and the
            // actual unlink — inside the destructive call, after every pre-check has passed.
            h.Deletes.BeforeDelete = file =>
            {
                if (file.Id == 21) MutateInPlace(first);
            };

            var result = AudioRun(h, route, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());

            Assert.True(result.DeleteFailures == 1, string.Join("\n", log.WarnMessages));
            Assert.Equal(1, result.Cleaned); // unmonitored before the failed deletion
            Assert.False(h.Single.Monitored);
            Assert.True(h.Source.Monitored);
            Assert.Single(h.Deletes.DeletedFiles);
            Assert.Equal(21, h.Deletes.DeletedFiles[0].TrackFile.Id);
            Assert.True(System.IO.File.Exists(second), "remaining candidate files must be preserved");
            Assert.True(System.IO.File.Exists(sourceMain) && System.IO.File.Exists(sourceSecond));
            Assert.Contains(log.WarnMessages, m => m.Contains("mutation-detected-post-delete"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactCleanup_SourceMutatedInsideDeleteCallStopsRemainingDeletions(int route)
    {
        if (OperatingSystem.IsWindows()) return; // POSIX delete-guard proof; Windows share enforcement blocks the simulated in-place writer itself.
        var (h, directory, first, second, sourceMain, sourceSecond) = GuardedDeleteHarness();
        using var log = h.Log;
        try
        {
            h.Deletes.BeforeDelete = file =>
            {
                if (file.Id == 21) MutateInPlace(sourceMain);
            };

            var result = AudioRun(h, route, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());

            // The first deletion completed before the mutation, but the next per-file
            // revalidation re-hashes every source and must abort the remaining deletion.
            Assert.True(result.DeleteFailures == 1, string.Join("\n", log.WarnMessages));
            Assert.Equal(1, result.Cleaned);
            Assert.False(h.Single.Monitored);
            Assert.True(h.Source.Monitored);
            Assert.Single(h.Deletes.DeletedFiles);
            Assert.True(System.IO.File.Exists(second), "remaining candidate files must be preserved");
            Assert.True(System.IO.File.Exists(sourceSecond));
            Assert.Contains(log.WarnMessages, m => m.Contains("source-content-changed"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExactCleanup_CandidateRemovedInsideDeleteCallFailsClosedBeforeNextDeletion(int route)
    {
        var (h, directory, first, second, _, _) = GuardedDeleteHarness();
        using var log = h.Log;
        try
        {
            // A sibling candidate file disappears inside the destructive window; the next
            // delete-time identity anchor cannot be opened, so cleanup must fail closed.
            h.Deletes.BeforeDelete = file =>
            {
                if (file.Id == 21) System.IO.File.Delete(second);
            };

            var result = AudioRun(h, route, new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions());

            Assert.True(result.DeleteFailures == 1, string.Join("\n", log.WarnMessages));
            Assert.Equal(1, result.Cleaned);
            Assert.False(h.Single.Monitored);
            Assert.Single(h.Deletes.DeletedFiles);
            Assert.Equal(21, h.Deletes.DeletedFiles[0].TrackFile.Id);
            Assert.DoesNotContain(h.Deletes.DeletedFiles, d => d.TrackFile.Id == 22);
            Assert.Contains(log.WarnMessages, m => m.Contains("delete-guard-open-failed"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Two-file exact-verified release backed by real temporary FLAC files, so delete-time guard
    /// semantics (open handle across the destructive call) are exercised against the real
    /// filesystem, matching the late-sample/mutation fixtures style of the other audio tests.
    /// </summary>
    private static (AudioTestHarness Harness, string Directory, string First, string Second, string SourceMain, string SourceSecond) GuardedDeleteHarness()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uniquesingles-delete-guard-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var first = System.IO.Path.Combine(directory, "candidate-a.flac");
        var second = System.IO.Path.Combine(directory, "candidate-b.flac");
        var sourceMain = System.IO.Path.Combine(directory, "source-main.flac");
        var sourceSecond = System.IO.Path.Combine(directory, "source-second.flac");
        System.IO.File.Copy(AudioFixture("original.flac"), first);
        System.IO.File.Copy(AudioFixture("stereo.flac"), second);
        System.IO.File.Copy(AudioFixture("same-tags.flac"), sourceMain);
        System.IO.File.Copy(AudioFixture("stereo.flac"), sourceSecond);

        var h = AudioHarness("same-tags.flac");
        var secondCandidate = Track("Other", 100000, "other-candidate", 22);
        secondCandidate.Id = 3;
        var secondSource = Track("Other", 900000, "other-source", 12);
        secondSource.Id = 4;
        h.Tracks.WithTracks(200, h.Candidate, secondCandidate).WithTracks(100, h.SourceTrack, secondSource);
        h.Files.WithFiles(200, File(21, 200, first), File(22, 200, second))
            .WithFiles(100, File(11, 100, sourceMain), File(12, 100, sourceSecond));
        h.Deletes.DeleteSyntheticFiles = true;
        return (h, directory, first, second, sourceMain, sourceSecond);
    }

    private static void MutateInPlace(string path)
    {
        // External same-inode write through a separate process: models an out-of-Lidarr writer
        // racing the destructive call. In-process .NET writers are blocked outright by the
        // delete guard's FileShare.Read handle, so the simulated external mutation must come
        // from another process, matching the documented threat model. cp truncates the
        // destination in place (same inode) rather than unlinking and replacing the path.
        var replacement = path + ".mutated";
        var bytes = System.IO.File.ReadAllBytes(path);
        bytes[^1] ^= 0x5a;
        System.IO.File.WriteAllBytes(replacement, bytes);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/cp", $"\"{replacement}\" \"{path}\""));
        process!.WaitForExit();
        Assert.True(process.ExitCode == 0, $"external in-place mutation failed with exit code {process.ExitCode}");
    }

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
    [InlineData("exclusive-undownloaded")]
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
            if (protection == "exclusive-undownloaded") h.Tracks.WithTracks(200, h.Candidate, Track("Exclusive", 9000, "exclusive", 0));
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
            if (protection.StartsWith("exclusive"))
            {
                Assert.Equal(0, result.ReviewNeeded);
                Assert.Contains(log.InfoMessages, m => m.Contains("keep-exclusive"));
            }
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

    /// <summary>
    /// One retained album (audio-equal counterpart), three keep-exclusive filler singles whose
    /// title-matching track consumes a comparison against different PCM, and a review-needed
    /// single listed LAST so only prioritization/reservation lets it get comparison attempts.
    /// </summary>
    private static ReviewPriorityTestHarness ReviewPriorityHarness()
    {
        var source = Album(100, "Retained", "Album");
        var sourceTrack = Track("Song", 900000, "source", 11);
        sourceTrack.Id = 1;
        var fillers = new List<Album>();
        for (var i = 0; i < 3; i++)
        {
            fillers.Add(Album(201 + i, "Filler " + i, "Single"));
        }

        var reviewSingle = Album(204, "Review Candidate", "Single");
        var reviewTrack = Track("Song", 100000, "review-candidate", 21);
        reviewTrack.Id = 20;

        var albums = new RecordingAlbumService(new[] { source }.Concat(fillers).Append(reviewSingle).ToArray());
        var tracks = new RecordingTrackService().WithTracks(100, sourceTrack);
        var files = new RecordingMediaFileService()
            .WithFiles(100, File(11, 100, AudioFixture("same-tags.flac")));
        var trackId = 10;
        foreach (var filler in fillers)
        {
            var fillerTrack = Track("Song", 100000, "filler-" + filler.Id, filler.Id * 10 + 1);
            fillerTrack.Id = trackId++;
            var exclusiveTrack = Track("Filler Exclusive " + filler.Id, 9000, "filler-exclusive-" + filler.Id, filler.Id * 10 + 2);
            exclusiveTrack.Id = trackId++;
            tracks.WithTracks(filler.Id, fillerTrack, exclusiveTrack);
            files.WithFiles(filler.Id,
                File(fillerTrack.TrackFileId, filler.Id, AudioFixture("late-sample.flac")),
                File(exclusiveTrack.TrackFileId, filler.Id, AudioFixture("late-sample.flac")));
        }

        tracks.WithTracks(204, reviewTrack);
        files.WithFiles(204, File(21, 204, AudioFixture("original.flac")));
        return new ReviewPriorityTestHarness(albums, tracks, files, new RecordingDeleteMediaFiles(),
            new RecordingLogger(), reviewSingle, source, reviewTrack);
    }

    private static CleanupResult ReviewPriorityRun(ReviewPriorityTestHarness h, int route, SingleCleanupOptions options)
    {
        var service = Service(h.Albums, h.Tracks, h.Files, h.Deletes, h.Log);
        if (route == 0) return service.ScanArtistWithOptions(Artist(), options);
        return new UniqueSinglesImportCoordinator(service, h.Log.Logger).HandleImport(
            new AlbumDownloadMessage { Artist = Artist(), Album = route == 1 ? h.ReviewSingle : h.Source }, options);
    }

    private sealed record ReviewPriorityTestHarness(RecordingAlbumService Albums, RecordingTrackService Tracks,
        RecordingMediaFileService Files, RecordingDeleteMediaFiles Deletes, RecordingLogger Log,
        Album ReviewSingle, Album Source, Track ReviewTrack);

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
