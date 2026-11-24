using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Exact-audio deletion guard, survivor side: the retained source must still exist with its
/// verified content after the destructive call (a concurrent removal would otherwise leave the
/// just-deleted candidate as the last verified copy), and a retained "source" that is only a
/// symlink to the candidate is not a second copy. Mutations run in a separate process, like the
/// candidate-side guard tests, because in-process writers are not the modelled threat.
/// </summary>
public partial class SingleCleanupServiceTests
{
    private static void RunExternal(string program, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardError = true };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{program} {string.Join(' ', args)} failed: {error}");
    }

    /// <summary>Single-file release backed by real temporary files: an exact-verified duplicate.</summary>
    private static (AudioTestHarness Harness, string Directory, string Candidate, string Source) SurvivorHarness()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uniquesingles-survivor-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var candidate = System.IO.Path.Combine(directory, "candidate.flac");
        var source = System.IO.Path.Combine(directory, "source.flac");
        System.IO.File.Copy(AudioFixture("original.flac"), candidate);
        System.IO.File.Copy(AudioFixture("same-tags.flac"), source);
        var h = AudioHarness("same-tags.flac");
        h.Files.WithFiles(100, File(11, 100, source)).WithFiles(200, File(21, 200, candidate));
        h.Deletes.DeleteSyntheticFiles = true;
        return (h, directory, candidate, source);
    }

    private static SingleCleanupOptions ExactCleanupOptions() => new UniqueSinglesSettings { ExactAudioCleanup = true }.ToCleanupOptions();

    [Theory]
    [InlineData("remove", 0)]
    [InlineData("remove", 1)]
    [InlineData("remove", 2)]
    [InlineData("rename-away", 0)]
    [InlineData("rename-away", 1)]
    [InlineData("rename-away", 2)]
    public void ExactCleanup_SourceRemovedInsideDeleteCall_IsDetectedNotSilentZeroSurvivors(string mutation, int route)
    {
        if (OperatingSystem.IsWindows()) return; // POSIX external-process race model.
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            h.Deletes.BeforeDelete = file =>
            {
                if (file.Id != 21) return;
                if (mutation == "remove") RunExternal("/bin/rm", source);
                else RunExternal("/bin/mv", source, source + ".moved-away");
            };

            var originalDigest = Sha256(candidate);

            var result = AudioRun(h, route, ExactCleanupOptions());

            // The vanished survivor must not be reported as a clean success: it is a counted
            // delete failure with a clear reason. The candidate went through Lidarr's deletion,
            // but its verified bytes are written back from the still-open guard handle, so the
            // race costs no audio.
            Assert.Equal(new[] { 21 }, h.Deletes.DeletedFiles.Select(d => d.TrackFile.Id).ToArray());
            Assert.Equal(1, result.DeleteFailures);
            Assert.Contains(log.WarnMessages, m => m.Contains("reason=source-vanished-post-delete") && m.Contains("remainingDeletions=aborted"));
            Assert.Equal(originalDigest, Sha256(candidate));
            Assert.Contains(log.WarnMessages, m => m.Contains("trackFileId=21") && m.Contains("rescue=restored") && m.Contains(candidate));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_SourceReplacedInsideDeleteCall_IsDetected()
    {
        if (OperatingSystem.IsWindows()) return;
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            h.Deletes.BeforeDelete = file =>
            {
                if (file.Id == 21) RunExternal("/bin/cp", AudioFixture("late-sample.flac"), source);
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Equal(1, result.DeleteFailures);
            Assert.Contains(log.WarnMessages, m => m.Contains("reason=source-content-changed-post-delete"));
            // The verified copy no longer exists anywhere else, so the candidate comes back too.
            Assert.Equal(Sha256(AudioFixture("original.flac")), Sha256(candidate));
            Assert.Contains(log.WarnMessages, m => m.Contains("rescue=restored"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_SourceVanishes_AndOriginalPathIsRetaken_RescuesToSiblingWithoutOverwriting()
    {
        if (OperatingSystem.IsWindows()) return;
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            var originalDigest = Sha256(candidate);
            h.Deletes.AfterDelete = file =>
            {
                if (file.Id != 21) return;
                RunExternal("/bin/rm", source);
                // Something else lands on the candidate's path before the rescue runs.
                System.IO.File.WriteAllText(candidate, "new occupant");
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            var sibling = System.IO.Path.Combine(directory, "candidate.rescued.flac");
            Assert.Equal(1, result.DeleteFailures);
            Assert.Equal("new occupant", System.IO.File.ReadAllText(candidate));
            Assert.Equal(originalDigest, Sha256(sibling));
            Assert.Contains(log.WarnMessages, m => m.Contains("rescue=restored") && m.Contains(sibling));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_SourceVanishes_AndNoRescueTargetIsFree_ReportsRescueFailedWithoutOverwriting()
    {
        if (OperatingSystem.IsWindows()) return;
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            var sibling = System.IO.Path.Combine(directory, "candidate.rescued.flac");
            h.Deletes.AfterDelete = file =>
            {
                if (file.Id != 21) return;
                RunExternal("/bin/rm", source);
                System.IO.File.WriteAllText(candidate, "first occupant");
                System.IO.File.WriteAllText(sibling, "second occupant");
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Equal(1, result.DeleteFailures);
            Assert.Equal("first occupant", System.IO.File.ReadAllText(candidate));
            Assert.Equal("second occupant", System.IO.File.ReadAllText(sibling));
            Assert.Contains(log.WarnMessages, m => m.Contains("rescue=failed") && m.Contains("trackFileId=21"));
            Assert.DoesNotContain(log.WarnMessages, m => m.Contains("rescue=restored"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_CandidateMutatedInsideDeleteCall_IsNotRescued()
    {
        if (OperatingSystem.IsWindows()) return;
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            // The pinned candidate bytes no longer match the verified digest, so they are not
            // the verified audio and must not be written back, even though the source also goes.
            h.Deletes.BeforeDelete = file =>
            {
                if (file.Id != 21) return;
                RunExternal("/bin/sh", "-c", "printf x >> \"$0\"", candidate);
                RunExternal("/bin/rm", source);
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Equal(1, result.DeleteFailures);
            Assert.Contains(log.WarnMessages, m => m.Contains("mutation-detected-post-delete"));
            Assert.False(System.IO.File.Exists(candidate));
            Assert.DoesNotContain(log.WarnMessages, m => m.Contains("rescue="));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void RescueTargets_TryOriginalPathThenSiblingKeepingTheAudioExtension()
    {
        var path = System.IO.Path.Combine("music", "Artist", "Single (2024)", "01 - Song.flac");

        Assert.Equal(
            new[] { path, System.IO.Path.Combine("music", "Artist", "Single (2024)", "01 - Song.rescued.flac") },
            SingleCleanupService.RescueTargets(path).ToArray());
    }

    private static string Sha256(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path)));

    [Fact]
    public void ExactCleanup_IntactSourceAfterDelete_StillSucceeds()
    {
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Equal(1, result.Cleaned);
            Assert.Equal(0, result.DeleteFailures);
            Assert.False(System.IO.File.Exists(candidate));
            Assert.True(System.IO.File.Exists(source));
            Assert.DoesNotContain(log.WarnMessages, m => m.Contains("post-delete"));
            Assert.DoesNotContain(log.WarnMessages, m => m.Contains("rescue="));
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(directory, "candidate.rescued.flac")));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("file-symlink", 0)]
    [InlineData("file-symlink", 1)]
    [InlineData("file-symlink", 2)]
    [InlineData("directory-symlink", 0)]
    [InlineData("chained-symlink", 0)]
    public void ExactCleanup_SourceSymlinkedToCandidate_IsNotASurvivor(string alias, int route)
    {
        if (OperatingSystem.IsWindows()) return; // Symlink creation needs privileges there.
        var (h, directory, candidate, _) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            string aliasPath;
            if (alias == "directory-symlink")
            {
                // source = <dir>/alias-dir/candidate.flac, where alias-dir -> <dir>: a different
                // path string for the very same file.
                System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(directory, "alias-dir"), directory);
                aliasPath = System.IO.Path.Combine(directory, "alias-dir", "candidate.flac");
            }
            else if (alias == "chained-symlink")
            {
                var first = System.IO.Path.Combine(directory, "hop1.flac");
                System.IO.File.CreateSymbolicLink(first, candidate);
                aliasPath = System.IO.Path.Combine(directory, "hop2.flac");
                System.IO.File.CreateSymbolicLink(aliasPath, first);
            }
            else
            {
                aliasPath = System.IO.Path.Combine(directory, "alias.flac");
                System.IO.File.CreateSymbolicLink(aliasPath, candidate);
            }

            h.Files.WithFiles(100, File(11, 100, aliasPath));

            var result = AudioRun(h, route, ExactCleanupOptions());

            Assert.Equal(0, result.Cleaned);
            Assert.Empty(h.Albums.SetMonitoredCalls);
            Assert.Empty(h.Deletes.DeletedFiles);
            Assert.True(System.IO.File.Exists(candidate), "the only real copy must survive");
            Assert.Contains(log.WarnMessages, m => m.Contains("source-shares-candidate-path"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_HardlinkedSource_IsStillAValidSurvivor()
    {
        if (OperatingSystem.IsWindows()) return;
        var (h, directory, candidate, _) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            // A hardlink is a second directory entry for the same data: removing the candidate
            // name leaves the data reachable through the source name.
            var hardlink = System.IO.Path.Combine(directory, "hardlink.flac");
            RunExternal("/bin/ln", candidate, hardlink);
            h.Files.WithFiles(100, File(11, 100, hardlink));

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Equal(1, result.Cleaned);
            Assert.Equal(0, result.DeleteFailures);
            Assert.False(System.IO.File.Exists(candidate));
            Assert.True(System.IO.File.Exists(hardlink));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_SymlinkToSeparateRealCopy_IsStillAValidSurvivor()
    {
        if (OperatingSystem.IsWindows()) return;
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            var link = System.IO.Path.Combine(directory, "link-to-source.flac");
            System.IO.File.CreateSymbolicLink(link, source);
            h.Files.WithFiles(100, File(11, 100, link));

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Equal(1, result.Cleaned);
            Assert.False(System.IO.File.Exists(candidate));
            Assert.True(System.IO.File.Exists(source));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResolveRealPath_FollowsFileDirectoryRelativeAndChainedLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uniquesingles-realpath-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(directory, "real"));
        try
        {
            var real = SingleCleanupService.ResolveRealPath(System.IO.Path.Combine(directory, "real"));
            var file = System.IO.Path.Combine(directory, "real", "a.flac");
            System.IO.File.WriteAllText(file, "x");
            System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(directory, "dirlink"), "real"); // relative target
            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(directory, "filelink"), "dirlink/a.flac");
            System.IO.File.CreateSymbolicLink(System.IO.Path.Combine(directory, "chain"), "filelink");

            var expected = System.IO.Path.Combine(real, "a.flac");
            Assert.Equal(expected, SingleCleanupService.ResolveRealPath(file));
            Assert.Equal(expected, SingleCleanupService.ResolveRealPath(System.IO.Path.Combine(directory, "dirlink", "a.flac")));
            Assert.Equal(expected, SingleCleanupService.ResolveRealPath(System.IO.Path.Combine(directory, "chain")));
            Assert.Equal(expected, SingleCleanupService.ResolveRealPath(System.IO.Path.Combine(directory, "real", "..", "dirlink", "a.flac")));
            Assert.Equal(
                System.IO.Path.Combine(real, "missing.flac"),
                SingleCleanupService.ResolveRealPath(System.IO.Path.Combine(directory, "dirlink", "missing.flac")));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResolveRealPath_SymlinkLoopFailsInsteadOfHanging()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uniquesingles-loop-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var a = System.IO.Path.Combine(directory, "a");
            var b = System.IO.Path.Combine(directory, "b");
            System.IO.File.CreateSymbolicLink(a, b);
            System.IO.File.CreateSymbolicLink(b, a);

            Assert.Throws<System.IO.IOException>(() => SingleCleanupService.ResolveRealPath(a));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }
}
