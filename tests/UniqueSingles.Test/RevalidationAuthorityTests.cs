using NzbDrone.Core.Music;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Exact-audio revalidation re-reads the candidate from the repository: a candidate that vanished
/// or whose secondary types changed after the cleanup decision withdraws delete authority instead
/// of the stale in-memory object being trusted, and a source vanishing mid-deletion stops the
/// remaining deletions of a multi-file release.
/// </summary>
public partial class SingleCleanupServiceTests
{
    // Lookup 1 is the scan's own read; lookup 2 the revalidation before the unmonitor; lookup 3
    // the per-file revalidation immediately before the destructive call.
    private const int RevalidationBeforeUnmonitor = 2;
    private const int RevalidationBeforeDelete = 3;

    private static Album FreshCopyWithSecondaryTypes(Album stale, List<SecondaryAlbumType> secondaryTypes) => new()
    {
        Id = stale.Id,
        Title = stale.Title,
        AlbumType = stale.AlbumType,
        Monitored = stale.Monitored,
        SecondaryTypes = secondaryTypes,
    };

    [Theory]
    [InlineData(RevalidationBeforeUnmonitor)]
    [InlineData(RevalidationBeforeDelete)]
    public void ExactCleanup_CandidateVanishesFromRepository_WithdrawsDeleteAuthority(int lookup)
    {
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            var lookups = 0;
            h.Albums.BeforeArtistLookup = () =>
            {
                if (++lookups == lookup) h.Albums.RemoveAlbum(h.Single.Id);
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Empty(h.Deletes.DeletedFiles);
            Assert.True(System.IO.File.Exists(candidate));
            Assert.True(System.IO.File.Exists(source));
            Assert.Contains(log.WarnMessages, m => m.Contains("reason=revalidation-failed") && m.Contains("candidate-missing"));
            if (lookup == RevalidationBeforeUnmonitor)
            {
                Assert.Equal(0, result.Cleaned);
                Assert.Empty(h.Albums.SetMonitoredCalls);
            }
            else
            {
                Assert.Equal(1, result.DeleteFailures);
            }
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(RevalidationBeforeUnmonitor)]
    [InlineData(RevalidationBeforeDelete)]
    public void ExactCleanup_CandidateSecondaryTypesChangeAfterDecision_WithdrawsDeleteAuthority(int lookup)
    {
        var (h, directory, candidate, source) = SurvivorHarness();
        using var log = h.Log;
        try
        {
            var lookups = 0;
            // The decision was made on h.Single (no secondary types). The repository now returns
            // a distinct instance marking it a Live version: only the fresh re-read sees it.
            h.Albums.BeforeArtistLookup = () =>
            {
                if (++lookups == lookup)
                {
                    h.Albums.ReplaceAlbum(FreshCopyWithSecondaryTypes(h.Single, new() { SecondaryAlbumType.Live }));
                }
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            Assert.Empty(h.Deletes.DeletedFiles);
            Assert.True(System.IO.File.Exists(candidate));
            Assert.True(System.IO.File.Exists(source));
            Assert.Contains(log.WarnMessages, m => m.Contains("reason=revalidation-failed") && m.Contains("candidate-secondary-type-protected"));
            if (lookup == RevalidationBeforeUnmonitor)
            {
                Assert.Equal(0, result.Cleaned);
                Assert.Empty(h.Albums.SetMonitoredCalls);
            }
            else
            {
                Assert.Equal(1, result.DeleteFailures);
            }
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExactCleanup_SourceVanishesDuringFirstOfTwoDeletions_AbortsAndKeepsTheRemainingFile()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uniquesingles-multifile-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var candidateOne = System.IO.Path.Combine(directory, "candidate-1.flac");
            var candidateTwo = System.IO.Path.Combine(directory, "candidate-2.flac");
            var sourceOne = System.IO.Path.Combine(directory, "source-1.flac");
            var sourceTwo = System.IO.Path.Combine(directory, "source-2.flac");
            System.IO.File.Copy(AudioFixture("original.flac"), candidateOne);
            System.IO.File.Copy(AudioFixture("stereo.flac"), candidateTwo);
            System.IO.File.Copy(AudioFixture("same-tags.flac"), sourceOne);
            System.IO.File.Copy(AudioFixture("stereo.flac"), sourceTwo);

            var h = AudioHarness("same-tags.flac");
            using var log = h.Log;
            var second = Track("Other", 100000, "other-candidate", 22);
            second.Id = 3;
            var otherSource = Track("Other", 900000, "other-source", 12);
            otherSource.Id = 4;
            h.Tracks.WithTracks(200, h.Candidate, second).WithTracks(100, h.SourceTrack, otherSource);
            h.Files.WithFiles(200, File(21, 200, candidateOne), File(22, 200, candidateTwo))
                .WithFiles(100, File(11, 100, sourceOne), File(12, 100, sourceTwo));
            h.Deletes.DeleteSyntheticFiles = true;
            h.Deletes.BeforeDelete = file =>
            {
                // The retained copy of the first track disappears inside its own delete call.
                if (file.Id == 21) System.IO.File.Delete(sourceOne);
            };

            var result = AudioRun(h, 0, ExactCleanupOptions());

            // The first deletion cannot be undone and is reported as a failure; the second file
            // was never touched.
            Assert.Equal(1, result.DeleteFailures);
            Assert.Equal(new[] { 21 }, h.Deletes.DeletedFiles.Select(d => d.TrackFile.Id).ToArray());
            Assert.False(System.IO.File.Exists(candidateOne));
            Assert.True(System.IO.File.Exists(candidateTwo));
            Assert.True(System.IO.File.Exists(sourceTwo));
            Assert.Contains(log.WarnMessages, m => m.Contains("trackFileId=21") && m.Contains("reason=source-vanished-post-delete") && m.Contains("remainingDeletions=aborted"));
            Assert.DoesNotContain(log.InfoMessages, m => m.Contains("delete success") && m.Contains("trackFileId=22"));
            // Aborted, not merely refused later: the second file is never even revalidated, so
            // the only per-file revalidation is the first file's.
            Assert.Single(log.InfoMessages, m => m.Contains("decision=revalidated action=delete"));
            Assert.DoesNotContain(log.WarnMessages, m => m.Contains("reason=revalidation-failed"));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }
}
