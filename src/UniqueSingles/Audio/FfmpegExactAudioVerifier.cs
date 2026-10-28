using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Optional local executable adapter. Decodes immutable bounded input snapshots, with no transforms.
/// Native FLAC channel order is defined by channel count. Only original signed 16/24-bit PCM is supported.
/// </summary>
public sealed class FfmpegExactAudioVerifier : IExactAudioVerifier
{
    private const int MaxInputBytes = 64 * 1024 * 1024;
    private const long MaxOutputBytes = 256 * 1024 * 1024;
    private static readonly SemaphoreSlim DecoderSlot = new(1, 1);
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public FfmpegExactAudioVerifier(string executable = "ffmpeg", TimeSpan? timeout = null)
    {
        _executable = executable;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public AudioEvidence Compare(string candidatePath, string sourcePath, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var entered = false;
        try
        {
            DecoderSlot.Wait(deadline.Token);
            entered = true;
            var candidate = Decode(candidatePath, deadline.Token).GetAwaiter().GetResult();
            var source = Decode(sourcePath, deadline.Token).GetAwaiter().GetResult();
            var equal = candidate.SampleRate == source.SampleRate && candidate.Channels == source.Channels
                && candidate.ChannelLayout == source.ChannelLayout
                && candidate.BitDepth == source.BitDepth && candidate.Samples == source.Samples
                && candidate.PcmDigest == source.PcmDigest;
            return new AudioEvidence(equal ? AudioEvidenceStatus.VerifiedExact : AudioEvidenceStatus.Different,
                equal ? "complete-original-pcm-equal" : "original-pcm-different", candidate, source);
        }
        catch (OperationCanceledException)
        {
            return new AudioEvidence(AudioEvidenceStatus.Unavailable, "cancelled-or-time-budget");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is ArgumentException)
        {
            // Never log executable stderr, paths or exception messages as structured evidence.
            return new AudioEvidence(AudioEvidenceStatus.Unavailable, "decode-unavailable-or-invalid:" + ex.GetType().Name);
        }
        finally
        {
            if (entered) DecoderSlot.Release();
        }
    }

    private async Task<DecodedAudio> Decode(string path, CancellationToken token)
    {
        if (!string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsupported format");
        // A snapshot prevents path replacement or a concurrent writer from changing the decoder input.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length < 42 || file.Length > MaxInputBytes) throw new InvalidOperationException("Input bound");
        var input = new byte[(int)file.Length];
        await file.ReadExactlyAsync(input, token);
        if (!input.AsSpan(0, 4).SequenceEqual("fLaC"u8) || (input[4] & 127) != 0
            || input[5] != 0 || input[6] != 0 || input[7] != 34)
            throw new InvalidOperationException("Invalid STREAMINFO");
        var packed = BinaryPrimitives.ReadUInt64BigEndian(input.AsSpan(18, 8));
        int rate = (int)(packed >> 44), channels = (int)((packed >> 41) & 7) + 1, bits = (int)((packed >> 36) & 31) + 1;
        long samples = (long)(packed & 0xfffffffffUL);
        if (rate <= 0 || (bits != 16 && bits != 24) || samples <= 0)
            throw new InvalidOperationException("Unsupported or unknown format/count");
        var expectedBytes = checked(samples * channels * (bits / 8));
        if (expectedBytes > MaxOutputBytes) throw new InvalidOperationException("Output bound");
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "info", "-nostats", "-noauto_conversion_filters", "-nostdin", "-xerror", "-err_detect", "crccheck+explode", "-threads", "1",
            "-f", "flac", "-i", "pipe:0", "-map", "0:a:0", "-af", "ashowinfo", "-vn", "-sn", "-dn", "-threads", "1", "-c:a", bits == 16 ? "pcm_s16le" : "pcm_s24le",
            "-f", bits == 16 ? "s16le" : "s24le", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Decoder unavailable");
        using var terminate = token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var writer = Task.Run(async () =>
        {
            try { await process.StandardInput.BaseStream.WriteAsync(input, token); }
            finally { process.StandardInput.Close(); }
        }, token);
        // Check every decoded frame BEFORE the PCM encoder. FFmpeg accepts frame rates/layouts
        // that disagree with STREAMINFO; trusting only that header would mislabel original audio.
        long frameSamples = 0;
        string? channelLayout = null;
        var stderr = Task.Run(async () =>
        {
            var chars = new char[4096];
            var line = new StringBuilder();
            int count;
            while ((count = await process.StandardError.ReadAsync(chars, token)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    if (chars[i] != '\n' && chars[i] != '\r')
                    {
                        if (line.Length >= 4096) throw new InvalidOperationException("Decoder diagnostic bound");
                        line.Append(chars[i]);
                        continue;
                    }
                    var text = line.ToString();
                    line.Clear();
                    if (!text.StartsWith("[Parsed_ashowinfo_", StringComparison.Ordinal)) continue;
                    var match = Regex.Match(text, @"fmt:(\S+) channels:(\d+) chlayout:(\S+) rate:(\d+) nb_samples:(\d+)");
                    if (!match.Success || match.Groups[1].Value != (bits == 16 ? "s16" : "s32")
                        || int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) != channels
                        || int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) != rate)
                        throw new InvalidOperationException("Decoded frame format disagrees with STREAMINFO");
                    var layout = match.Groups[3].Value;
                    if (channelLayout != null && layout != channelLayout) throw new InvalidOperationException("Layout changed");
                    channelLayout = layout;
                    frameSamples += long.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
                }
            }
        }, token);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = new byte[65536];
            long bytes = 0;
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token)) > 0)
            {
                bytes += read;
                if (bytes > expectedBytes) throw new InvalidOperationException("Unexpected samples");
                sha.AppendData(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
            }
            await writer;
            await process.WaitForExitAsync(token);
            await stderr;
            if (process.ExitCode != 0 || bytes != expectedBytes || frameSamples != samples || channelLayout == null) throw new InvalidOperationException("Incomplete decode");
            var actualMd5 = md5.GetHashAndReset();
            var storedMd5 = input.AsSpan(26, 16).ToArray();
            if (storedMd5.Any(b => b != 0) && !actualMd5.SequenceEqual(storedMd5))
                throw new InvalidOperationException("STREAMINFO digest disagrees with decoded PCM");
            token.ThrowIfCancellationRequested();
            return new DecodedAudio(Convert.ToHexString(SHA256.HashData(input)), Convert.ToHexString(sha.GetHashAndReset()), rate, channels, bits, samples, channelLayout);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            // Join all pipe operations before disposing the process, including error/cancellation paths.
            try { await writer; } catch (Exception) { }
            try { await stderr; } catch (Exception) { }
        }
    }
}
