using System.IO.Abstractions;
using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Disk;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

public class SingleFolderCleanupTests : IDisposable
{
    private readonly string _root;
    private readonly string _artistPath;
    private readonly string _singleFolder;
    private readonly RecordingRecycleBin _recycleBin = new();
    private readonly MediaFileServiceProxy _mediaFiles;
    private readonly MemoryTarget _logTarget = new() { Layout = "${level}|${message}" };
    private readonly SingleFolderCleanup _cleanup;

    public SingleFolderCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "us-folder-" + Guid.NewGuid().ToString("N"));
        _artistPath = Path.Combine(_root, "PVRIS");
        _singleFolder = Path.Combine(_artistPath, "Single", "(2024) Burn the Witch");
        Directory.CreateDirectory(_singleFolder);

        _mediaFiles = (MediaFileServiceProxy)(object)DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();

        var factory = new LogFactory();
        var config = new LoggingConfiguration();
        config.AddRuleForAllLevels(_logTarget);
        factory.Configuration = config;

        _cleanup = new SingleFolderCleanup(
            new TestDiskProvider(),
            _recycleBin,
            (IMediaFileService)(object)_mediaFiles,
            factory.GetLogger("test"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void RemoveLeftovers_RecyclesSidecarsAndRemovesFolder()
    {
        var deleted = DeletedAudio("01 Burn the Witch.flac");
        Touch("album.nfo", "cover.jpg", "01 Burn the Witch.lrc");

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.False(Directory.Exists(_singleFolder));
        Assert.Equal(3, _recycleBin.Deleted.Count);
        Assert.All(_recycleBin.Deleted, d => Assert.Equal(Path.Combine("PVRIS", "Single", "(2024) Burn the Witch"), d.Subfolder));
        Assert.True(Directory.Exists(Path.Combine(_artistPath, "Single")));
        Assert.Contains(Logs(), l => l.Contains("folder removed", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_EmptyFolder_IsRemoved()
    {
        var deleted = DeletedAudio("01 Burn the Witch.flac");

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.False(Directory.Exists(_singleFolder));
        Assert.Empty(_recycleBin.Deleted);
    }

    [Fact]
    public void RemoveLeftovers_UntrackedAudioLeft_KeepsFolder()
    {
        var deleted = DeletedAudio("01 Burn the Witch.flac");
        Touch("cover.jpg", "02 Bonus.mp3");

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(File.Exists(Path.Combine(_singleFolder, "cover.jpg")));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains("reason=untracked-audio", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_OtherTrackedFilesInFolder_KeepsFolder()
    {
        var deleted = DeletedAudio("01 Burn the Witch.flac");
        Touch("cover.jpg");
        _mediaFiles.Tracked.Add(new TrackFile { Id = 2, Path = Path.Combine(_singleFolder, "other.flac") });

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(Directory.Exists(_singleFolder));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains("reason=other-tracked-files", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_Subfolders_KeepsFolder()
    {
        var deleted = DeletedAudio("01 Burn the Witch.flac");
        Directory.CreateDirectory(Path.Combine(_singleFolder, "Scans"));

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(Directory.Exists(_singleFolder));
        Assert.Contains(Logs(), l => l.Contains("reason=has-subfolders", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_SingleStoredInArtistFolder_LeavesArtistFolder()
    {
        var deleted = new List<TrackFile> { new() { Id = 1, Path = Path.Combine(_artistPath, "Burn the Witch.flac") } };
        File.WriteAllText(Path.Combine(_artistPath, "artist.nfo"), "x");

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(File.Exists(Path.Combine(_artistPath, "artist.nfo")));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains("reason=outside-artist-folder", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_RecycleFailure_IsLoggedNotThrown()
    {
        var deleted = DeletedAudio("01 Burn the Witch.flac");
        Touch("cover.jpg");
        _recycleBin.Throw = true;

        var exception = Record.Exception(() => _cleanup.RemoveLeftovers(Artist(), Single(), deleted));

        Assert.Null(exception);
        Assert.True(Directory.Exists(_singleFolder));
        Assert.Contains(Logs(), l => l.StartsWith("Warn|", StringComparison.Ordinal) && l.Contains("reason=folder-cleanup-failed", StringComparison.Ordinal));
    }

    private List<TrackFile> DeletedAudio(string name) =>
        new() { new TrackFile { Id = 1, Path = Path.Combine(_singleFolder, name) } };

    private void Touch(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(_singleFolder, name), "x");
        }
    }

    private Artist Artist() => new() { Id = 7, Name = "PVRIS", Path = _artistPath };

    private static Album Single() => new() { Id = 9, Title = "Burn the Witch", AlbumType = "Single" };

    private IList<string> Logs() => _logTarget.Logs;

    private sealed class RecordingRecycleBin : IRecycleBinProvider
    {
        public bool Throw { get; set; }

        public List<(string Path, string Subfolder)> Deleted { get; } = new();

        public void DeleteFile(string path, string subfolder = "")
        {
            if (Throw)
            {
                throw new IOException("recycle failed");
            }

            Deleted.Add((path, subfolder));
            File.Delete(path);
        }

        public void DeleteFolder(string path) => throw new NotSupportedException();

        public void Empty() => throw new NotSupportedException();

        public void Cleanup() => throw new NotSupportedException();
    }

    public class MediaFileServiceProxy : DispatchProxy
    {
        public List<TrackFile> Tracked { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMediaFileService.GetFilesWithBasePath))
            {
                var basePath = (string)args![0]!;
                return Tracked.Where(f => f.Path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class TestDiskProvider : DiskProviderBase
    {
        public TestDiskProvider()
            : base(new FileSystem())
        {
        }

        public override long? GetAvailableSpace(string path) => null;
        public override void InheritFolderPermissions(string filename)
        {
        }

        public override void SetEveryonePermissions(string filename)
        {
        }

        public override void SetFilePermissions(string path, string mask, string group)
        {
        }

        public override void SetPermissions(string path, string mask, string group)
        {
        }

        public override void CopyPermissions(string sourcePath, string targetPath)
        {
        }

        public override long? GetTotalSize(string path) => null;
        public override bool TryCreateHardLink(string source, string destination) => false;
    }
}
