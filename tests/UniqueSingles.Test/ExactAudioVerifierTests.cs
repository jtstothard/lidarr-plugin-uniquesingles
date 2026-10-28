using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

public class ExactAudioVerifierTests
{
    [Theory]
    [InlineData("same-tags.flac")]
    [InlineData("zero-md5.flac")]
    public void FullDecodeOriginalSamples_IndependentOfContainerAndOptionalMd5(string source)
    {
        var evidence = new FfmpegExactAudioVerifier().Compare(Fixture("original.flac"), Fixture(source), default);
        Assert.Equal(AudioEvidenceStatus.VerifiedExact, evidence.Status);
        Assert.Equal(12000, evidence.Candidate!.Samples);
        Assert.Equal(1500, evidence.Source!.DurationMs);
        Assert.Equal(16, evidence.Source.BitDepth);
        Assert.Equal(8000, evidence.Source.SampleRate);
        Assert.Equal(1, evidence.Source.Channels);
        Assert.NotEqual(evidence.Candidate.FileDigest, evidence.Source.FileDigest);
        Assert.Equal(evidence.Candidate.PcmDigest, evidence.Source.PcmDigest);
    }

    [Fact]
    public void FullDecode_Preserves24BitSamples()
    {
        var evidence = new FfmpegExactAudioVerifier().Compare(Fixture("different-depth.flac"), Fixture("24-tags.flac"), default);
        Assert.Equal(AudioEvidenceStatus.VerifiedExact, evidence.Status);
        Assert.Equal(24, evidence.Candidate!.BitDepth);
    }

    [Fact]
    public void DeadlineCannotReturnVerification()
    {
        var evidence = new FfmpegExactAudioVerifier(timeout: TimeSpan.Zero).Compare(Fixture("original.flac"), Fixture("original.flac"), default);
        Assert.Equal(AudioEvidenceStatus.Unavailable, evidence.Status);
    }

    [Fact]
    public async Task BusyDecoderSlot_DeadlineAndActiveCancellationReturnUnavailable()
    {
        // Test the actual process/pipe boundary without depending on a slow media fixture.
        if (OperatingSystem.IsWindows()) return; // POSIX process-lifecycle proof; Windows remains unqualified.
        var directory = Path.Combine(Path.GetTempPath(), "uniquesingles-39-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "decoder");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf ready > \"$0.ready\"\nexec /bin/sleep 60\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory) { EnableRaisingEvents = true };
        watcher.Created += (_, args) => { if (args.Name == "decoder.ready") ready.TrySetResult(); };
        using var cancel = new CancellationTokenSource();
        var running = Task.Run(() => new FfmpegExactAudioVerifier(executable).Compare(Fixture("original.flac"), Fixture("original.flac"), cancel.Token));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiting = new FfmpegExactAudioVerifier(timeout: TimeSpan.FromMilliseconds(50))
                .Compare(Fixture("original.flac"), Fixture("original.flac"), default);
            Assert.Equal(AudioEvidenceStatus.Unavailable, waiting.Status);
            cancel.Cancel();
            Assert.Equal(AudioEvidenceStatus.Unavailable, (await running.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            var recovered = new FfmpegExactAudioVerifier().Compare(Fixture("original.flac"), Fixture("same-tags.flac"), default);
            Assert.Equal(AudioEvidenceStatus.VerifiedExact, recovered.Status);
        }
        finally
        {
            cancel.Cancel();
            await running;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void VerifiedContentIsReusedButReplacementAndDisappearanceAreNot()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "uniquesingles-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var decoder = Path.Combine(directory, "decoder");
        var candidate = Path.Combine(directory, "candidate.flac");
        var source = Path.Combine(directory, "source.flac");
        File.WriteAllText(decoder, "#!/bin/sh\necho decode >> \"$0.calls\"\nexec ffmpeg \"$@\"\n");
        File.SetUnixFileMode(decoder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Copy(Fixture("original.flac"), candidate);
        File.Copy(Fixture("same-tags.flac"), source);
        try
        {
            var verifier = new FfmpegExactAudioVerifier(decoder);
            Assert.Equal(AudioEvidenceStatus.VerifiedExact, verifier.Compare(candidate, source, default).Status);
            var calls = File.ReadAllLines(decoder + ".calls").Length;
            Assert.Equal(AudioEvidenceStatus.VerifiedExact, verifier.Compare(candidate, source, default).Status);
            Assert.Equal(calls, File.ReadAllLines(decoder + ".calls").Length);
            var modified = File.GetLastWriteTimeUtc(source);
            File.Copy(Fixture("late-sample.flac"), source, true);
            File.SetLastWriteTimeUtc(source, modified);
            Assert.Equal(AudioEvidenceStatus.Different, verifier.Compare(candidate, source, default).Status);
            File.Copy(Fixture("same-tags.flac"), source, true);
            File.WriteAllText(decoder, "#!/bin/sh\nexit 1\n");
            Assert.Equal(AudioEvidenceStatus.Unavailable, verifier.Compare(candidate, source, default).Status);
            File.Delete(source);
            Assert.Equal(AudioEvidenceStatus.Unavailable, verifier.Compare(candidate, source, default).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MutationDuringDecodeCannotApproveSnapshots(bool useCache)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "uniquesingles-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var decoder = Path.Combine(directory, "decoder");
        var candidate = Path.Combine(directory, "candidate.flac");
        System.IO.File.Copy(Fixture("original.flac"), candidate);
        System.IO.File.Copy(Fixture("late-sample.flac"), Path.Combine(directory, "replacement.flac"));
        System.IO.File.WriteAllText(decoder, "#!/bin/sh\ncp \"${0%/*}/replacement.flac\" \"${0%/*}/candidate.flac\"\nexec ffmpeg \"$@\"\n");
        System.IO.File.SetUnixFileMode(decoder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(AudioEvidenceStatus.Unavailable, new FfmpegExactAudioVerifier(decoder, useCache: useCache)
                .Compare(candidate, Fixture("same-tags.flac"), default).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("same-tags.flac")]
    [InlineData("late-sample.flac")]
    [InlineData("forged-md5.flac")]
    public void CachedAndUncachedEvidenceAgree(string source)
    {
        var uncached = new FfmpegExactAudioVerifier(useCache: false).Compare(Fixture("original.flac"), Fixture(source), default);
        var verifier = new FfmpegExactAudioVerifier();
        Assert.Equal(uncached, verifier.Compare(Fixture("original.flac"), Fixture(source), default));
        Assert.Equal(uncached, verifier.Compare(Fixture("original.flac"), Fixture(source), default));
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "AudioFixtures", name);
}
