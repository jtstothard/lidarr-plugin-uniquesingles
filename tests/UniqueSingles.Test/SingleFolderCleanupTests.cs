using System.IO.Abstractions;
using System.Reflection;
using DryIoc;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins;
using Xunit;

namespace UniqueSingles.Test;

/// <summary>
/// Folder cleanup against real temporary folders through Lidarr's DiskProviderBase.
/// </summary>
public class SingleFolderCleanupTests : IDisposable
{
    private static readonly DateTime UnmonitoredAt = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly string _artistPath;
    private readonly string _singleFolder;
    private readonly RecordingRecycleBin _recycleBin;
    private readonly MediaFileServiceProxy _mediaFiles;
    private readonly TrackServiceProxy _tracks;
    private readonly HistoryRepositoryProxy _history;
    private readonly MemoryTarget _logTarget = new() { Layout = "${level}|${message}" };
    private readonly SingleFolderCleanup _cleanup;

    public SingleFolderCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "us-folder-" + Guid.NewGuid().ToString("N"));
        _artistPath = Path.Combine(_root, "Music", "PVRIS");
        _singleFolder = Path.Combine(_artistPath, "Single", "(2024) Burn the Witch");
        Directory.CreateDirectory(_singleFolder);

        _recycleBin = new RecordingRecycleBin(Path.Combine(_root, "Recycle"));
        _mediaFiles = (MediaFileServiceProxy)(object)DispatchProxy.Create<IMediaFileService, MediaFileServiceProxy>();
        _tracks = (TrackServiceProxy)(object)DispatchProxy.Create<ITrackService, TrackServiceProxy>();
        _history = (HistoryRepositoryProxy)(object)DispatchProxy.Create<IHistoryRepository, HistoryRepositoryProxy>();

        var factory = new LogFactory();
        var config = new LoggingConfiguration();
        config.AddRuleForAllLevels(_logTarget);
        factory.Configuration = config;

        _cleanup = new SingleFolderCleanup(
            new TestDiskProvider(),
            _recycleBin,
            (IMediaFileService)(object)_mediaFiles,
            (ITrackService)(object)_tracks,
            (IHistoryRepository)(object)_history,
            factory.GetLogger("test"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    // ============================================================
    // Removal after delete
    // ============================================================

    [Fact]
    public void RemoveLeftovers_RecognizedSidecars_AreRecycledNextToTheAudioAndFolderRemoved()
    {
        var deleted = DeletedAudio(_singleFolder, "01 Burn the Witch.flac");
        Touch(_singleFolder, "album.nfo", "cover.jpg", "Folder.PNG", "01 Burn the Witch.lrc", ".DS_Store");

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.False(Directory.Exists(_singleFolder));
        Assert.True(Directory.Exists(Path.Combine(_artistPath, "Single")));
        Assert.Equal(5, _recycleBin.Deleted.Count);

        // MediaFileDeletionService: GetParentFolder(artist.Path).GetRelativePath(GetParentFolder(audio)).
        var audioSubfolder = Path.Combine("PVRIS", "Single", "(2024) Burn the Witch");
        Assert.All(_recycleBin.Deleted, d => Assert.Equal(audioSubfolder, d.Subfolder));
        Assert.True(File.Exists(Path.Combine(_recycleBin.Root, audioSubfolder, "cover.jpg")));
        Assert.Contains(Logs(), l => l.Contains("folder removed", StringComparison.Ordinal) && l.Contains("recycledSidecars=5", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_EmptyFolder_IsRemoved()
    {
        _cleanup.RemoveLeftovers(Artist(), Single(), DeletedAudio(_singleFolder, "01 Burn the Witch.flac"));

        Assert.False(Directory.Exists(_singleFolder));
        Assert.Empty(_recycleBin.Deleted);
    }

    [Theory]
    [InlineData("music-video.mp4", "unrecognized-files")]
    [InlineData("music-video.mkv", "unrecognized-files")]
    [InlineData("booklet.pdf", "unrecognized-files")]
    [InlineData("Burn the Witch.cue", "unrecognized-files")]
    [InlineData("rip.log", "unrecognized-files")]
    [InlineData("notes.txt", "unrecognized-files")]
    [InlineData("back.jpg", "unrecognized-files")]
    [InlineData("playlist.pls", "unrecognized-files")]
    [InlineData("playlist.m3u", "unrecognized-files")]
    [InlineData("playlist.m3u8", "unrecognized-files")]
    [InlineData("Favourites.M3U8", "unrecognized-files")]
    [InlineData(".my-hidden-notes", "unrecognized-files")]
    [InlineData("02 Bonus.mp3", "untracked-audio")]
    public void RemoveLeftovers_UnrecognizedContent_KeepsFolderAndEveryFile(string userFile, string reason)
    {
        // No recycle bin configured (Lidarr's default): anything handed to it is gone for good.
        _recycleBin.Configured = false;
        var deleted = DeletedAudio(_singleFolder, "01 Burn the Witch.flac");
        Touch(_singleFolder, "cover.jpg", "album.nfo", userFile);

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.Empty(_recycleBin.Deleted);
        Assert.True(File.Exists(Path.Combine(_singleFolder, userFile)));
        Assert.True(File.Exists(Path.Combine(_singleFolder, "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(_singleFolder, "album.nfo")));
        Assert.Contains(Logs(), l => l.StartsWith("Info|", StringComparison.Ordinal)
            && l.Contains("reason=" + reason, StringComparison.Ordinal)
            && l.Contains(userFile, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveLeftovers_ContentAppearingAfterTheChecks_SurvivesAndFolderIsKept(bool withSubfolder)
    {
        // The race the checks cannot close: something lands in the folder after the last
        // inspection and the sidecar recycling, right before the folder is removed. Lidarr's
        // DiskProviderBase.DeleteFolder(path, false) would permanently delete a top-level file
        // here and force-delete any subfolder with it.
        var deleted = DeletedAudio(_singleFolder, "01 Burn the Witch.flac");
        Touch(_singleFolder, "cover.jpg");
        var late = new List<string> { Path.Combine(_singleFolder, "interview.mkv") };
        if (withSubfolder)
        {
            late.Add(Path.Combine(_singleFolder, "Extras", "making-of.mkv"));
        }

        _cleanup.BeforeFolderDelete = folder =>
        {
            foreach (var file in late)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "user content");
            }
        };

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.All(late, file => Assert.Equal("user content", File.ReadAllText(file)));
        Assert.True(Directory.Exists(_singleFolder));
        Assert.Equal(new[] { "cover.jpg" }, _recycleBin.Deleted.Select(d => Path.GetFileName(d.Path)));
        Assert.Contains(Logs(), l => l.Contains("reason=not-empty-at-delete", StringComparison.Ordinal));
        Assert.DoesNotContain(Logs(), l => l.Contains("folder removed", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_OtherTrackedFilesInFolder_KeepsFolder()
    {
        var deleted = DeletedAudio(_singleFolder, "01 Burn the Witch.flac");
        Touch(_singleFolder, "cover.jpg");
        _mediaFiles.Tracked.Add(new TrackFile { Id = 2, Path = Path.Combine(_singleFolder, "other.flac") });

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(File.Exists(Path.Combine(_singleFolder, "cover.jpg")));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains("reason=other-tracked-files", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_Subfolders_KeepsFolderWithoutRecyclingAnything()
    {
        var deleted = DeletedAudio(_singleFolder, "01 Burn the Witch.flac");
        Touch(_singleFolder, "cover.jpg");
        Directory.CreateDirectory(Path.Combine(_singleFolder, "Scans"));

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(File.Exists(Path.Combine(_singleFolder, "cover.jpg")));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains("reason=has-subfolders", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_SingleStoredInArtistFolder_LeavesArtistFolder()
    {
        var deleted = DeletedAudio(_artistPath, "Burn the Witch.flac");
        Touch(_artistPath, "artist.nfo");

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.True(File.Exists(Path.Combine(_artistPath, "artist.nfo")));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains("reason=outside-artist-folder", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_RecycleFailure_IsLoggedNotThrown()
    {
        var deleted = DeletedAudio(_singleFolder, "01 Burn the Witch.flac");
        Touch(_singleFolder, "cover.jpg");
        _recycleBin.Throw = true;

        var exception = Record.Exception(() => _cleanup.RemoveLeftovers(Artist(), Single(), deleted));

        Assert.Null(exception);
        Assert.True(File.Exists(Path.Combine(_singleFolder, "cover.jpg")));
        Assert.Contains(Logs(), l => l.StartsWith("Warn|", StringComparison.Ordinal) && l.Contains("reason=folder-cleanup-failed", StringComparison.Ordinal));
    }

    // ============================================================
    // Multi-folder layouts
    // ============================================================

    [Fact]
    public void RemoveLeftovers_DiscFolders_AreCleanedInnermostFirstThenTheReleaseFolder()
    {
        var release = _singleFolder;
        var cd1 = Path.Combine(release, "CD 01");
        var cd2 = Path.Combine(release, "CD 02");
        Directory.CreateDirectory(cd1);
        Directory.CreateDirectory(cd2);
        Touch(release, "folder.jpg", "album.nfo");
        Touch(cd1, "01 Burn the Witch.lrc");
        _tracks.Tracks = TwoDiscTracks();
        var deleted = DeletedAudio(cd2, "01 Remix.flac").Concat(DeletedAudio(cd1, "01 Burn the Witch.flac")).ToList();

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.False(Directory.Exists(release));
        Assert.True(Directory.Exists(Path.Combine(_artistPath, "Single")));
        Assert.Equal(new[] { cd1, cd2, release }, RemovedFolders());
        Assert.Contains(_recycleBin.Deleted, d => Path.GetFileName(d.Path) == "01 Burn the Witch.lrc" && d.Subfolder == Path.Combine("PVRIS", "Single", "(2024) Burn the Witch", "CD 01"));
        Assert.Contains(_recycleBin.Deleted, d => Path.GetFileName(d.Path) == "folder.jpg" && d.Subfolder == Path.Combine("PVRIS", "Single", "(2024) Burn the Witch"));
    }

    [Fact]
    public void RemoveLeftovers_DiscFolderWithUserContent_KeepsItAndTheReleaseFolderUntouched()
    {
        var release = _singleFolder;
        var cd1 = Path.Combine(release, "CD 01");
        var cd2 = Path.Combine(release, "CD 02");
        Directory.CreateDirectory(cd1);
        Directory.CreateDirectory(cd2);
        Touch(release, "folder.jpg");
        Touch(cd1, "making-of.mkv");
        _tracks.Tracks = TwoDiscTracks();
        var deleted = DeletedAudio(cd1, "01 Burn the Witch.flac").Concat(DeletedAudio(cd2, "01 Remix.flac")).ToList();

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.Equal(new[] { cd2 }, RemovedFolders());
        Assert.True(File.Exists(Path.Combine(cd1, "making-of.mkv")));
        Assert.True(File.Exists(Path.Combine(release, "folder.jpg")));
        Assert.Empty(_recycleBin.Deleted);
        Assert.Contains(Logs(), l => l.Contains($"folder='{release}'", StringComparison.Ordinal) && l.Contains("reason=has-subfolders", StringComparison.Ordinal));
    }

    [Fact]
    public void RemoveLeftovers_AudioInReleaseAndDiscFolder_CleansDiscThenRelease()
    {
        var release = _singleFolder;
        var cd2 = Path.Combine(release, "CD 02");
        Directory.CreateDirectory(cd2);
        Touch(release, "cover.jpg");
        _tracks.Tracks = TwoDiscTracks();
        var deleted = DeletedAudio(release, "01 Burn the Witch.flac").Concat(DeletedAudio(cd2, "01 Remix.flac")).ToList();

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.Equal(new[] { cd2, release }, RemovedFolders());
    }

    [Fact]
    public void RemoveLeftovers_SiblingFoldersOfASingleDiscSingle_DoNotBringInTheContainer()
    {
        // Two audio folders under Artist/Singles (a renamed album folder, or several cycles combined)
        // are not discs of one release: the container's own files are not the single's.
        var container = Path.Combine(_artistPath, "Singles");
        var first = Path.Combine(container, "(2024) Burn the Witch");
        var second = Path.Combine(container, "(2024) Burn the Witch (Remastered)");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Touch(container, "folder.jpg", "artist.nfo");
        Touch(first, "cover.jpg");
        _tracks.Tracks = new List<Track> { new() { MediumNumber = 1 }, new() { MediumNumber = 1 } };
        var deleted = DeletedAudio(first, "01 Burn the Witch.flac").Concat(DeletedAudio(second, "01 Burn the Witch.flac")).ToList();

        _cleanup.RemoveLeftovers(Artist(), Single(), deleted);

        Assert.Equal(new[] { first, second }.OrderBy(f => f, StringComparer.Ordinal), RemovedFolders().OrderBy(f => f, StringComparer.Ordinal));
        Assert.True(Directory.Exists(container));
        Assert.True(File.Exists(Path.Combine(container, "folder.jpg")));
        Assert.True(File.Exists(Path.Combine(container, "artist.nfo")));
        Assert.DoesNotContain(_recycleBin.Deleted, d => Path.GetDirectoryName(d.Path) == container);
    }

    [Fact]
    public void RemoveLeftovers_SiblingFoldersWithNoTrackData_DoNotBringInTheParent()
    {
        var container = Path.Combine(_artistPath, "2020");
        var first = Path.Combine(container, "A");
        var second = Path.Combine(container, "B");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Touch(container, "folder.jpg");

        _cleanup.RemoveLeftovers(Artist(), Single(), DeletedAudio(first, "01.flac").Concat(DeletedAudio(second, "01.flac")).ToList());

        Assert.True(File.Exists(Path.Combine(container, "folder.jpg")));
        Assert.DoesNotContain(RemovedFolders(), f => f == container);
    }

    [Fact]
    public void RemoveLeftovers_MultiDiscFoldersNotDirectChildrenOfOneParent_DoNotBringInAParent()
    {
        // Two discs, but the audio folders do not share a parent: nothing is inferred above them.
        var cd1 = Path.Combine(_singleFolder, "CD 01");
        var cd2 = Path.Combine(_singleFolder, "Extra", "CD 02");
        Directory.CreateDirectory(cd1);
        Directory.CreateDirectory(cd2);
        Touch(_singleFolder, "folder.jpg");
        _tracks.Tracks = TwoDiscTracks();

        _cleanup.RemoveLeftovers(Artist(), Single(), DeletedAudio(cd1, "01.flac").Concat(DeletedAudio(cd2, "01.flac")).ToList());

        Assert.Equal(new[] { cd2, cd1 }.OrderBy(f => f, StringComparer.Ordinal), RemovedFolders().OrderBy(f => f, StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(_singleFolder, "folder.jpg")));
    }

    // ============================================================
    // Sweep for folders stranded by earlier versions
    // ============================================================

    [Fact]
    public void SweepStrandedFolders_PluginCleanedSingle_RemovesItsSidecarOnlyFolder()
    {
        Touch(_singleFolder, "cover.jpg", "01 Burn the Witch.lrc");
        _history.Rows.Add(PluginUnmonitor(Single().Id, UnmonitoredAt));
        _history.Rows.Add(Deleted(Single().Id, Path.Combine(_singleFolder, "01 Burn the Witch.flac"), UnmonitoredAt.AddSeconds(4)));

        _cleanup.SweepStrandedFolders(Artist(), new[] { Single(), Album(10, "Album", "Album", monitored: true) });

        Assert.False(Directory.Exists(_singleFolder));
        Assert.Equal(2, _recycleBin.Deleted.Count);
        Assert.Contains(Logs(), l => l.Contains("folder removed", StringComparison.Ordinal) && l.Contains("trigger=sweep", StringComparison.Ordinal));
    }

    public enum SweepMiss
    {
        NoPluginUnmonitorRow,
        UnmonitorRowFromSomethingElse,
        SingleMonitoredAgain,
        NotASingle,
        DeletedForUpgrade,
        DeletedLongAfterUnmonitor,
        DeletedBeforeUnmonitor,
        OtherArtistsHistoryOnly,
        AudioStillInFolder,
        UnrecognizedFileInFolder,
    }

    [Theory]
    [InlineData(SweepMiss.NoPluginUnmonitorRow)]
    [InlineData(SweepMiss.UnmonitorRowFromSomethingElse)]
    [InlineData(SweepMiss.SingleMonitoredAgain)]
    [InlineData(SweepMiss.NotASingle)]
    [InlineData(SweepMiss.DeletedForUpgrade)]
    [InlineData(SweepMiss.DeletedLongAfterUnmonitor)]
    [InlineData(SweepMiss.DeletedBeforeUnmonitor)]
    [InlineData(SweepMiss.OtherArtistsHistoryOnly)]
    [InlineData(SweepMiss.AudioStillInFolder)]
    [InlineData(SweepMiss.UnrecognizedFileInFolder)]
    public void SweepStrandedFolders_WithoutSafeAttribution_LeavesFolderAlone(SweepMiss miss)
    {
        Touch(_singleFolder, "cover.jpg", "01 Burn the Witch.lrc");
        var single = Single();
        var unmonitor = PluginUnmonitor(single.Id, UnmonitoredAt);
        var deletion = Deleted(single.Id, Path.Combine(_singleFolder, "01 Burn the Witch.flac"), UnmonitoredAt.AddSeconds(4));

        switch (miss)
        {
            case SweepMiss.NoPluginUnmonitorRow:
                unmonitor = null;
                break;
            case SweepMiss.UnmonitorRowFromSomethingElse:
                unmonitor!.Data["Plugin"] = "SomeOtherPlugin";
                break;
            case SweepMiss.SingleMonitoredAgain:
                single.Monitored = true;
                break;
            case SweepMiss.NotASingle:
                single.AlbumType = "EP";
                break;
            case SweepMiss.DeletedForUpgrade:
                deletion.Data["Reason"] = nameof(DeleteMediaFileReason.Upgrade);
                break;
            case SweepMiss.DeletedLongAfterUnmonitor:
                deletion.Date = UnmonitoredAt.AddHours(3);
                break;
            case SweepMiss.DeletedBeforeUnmonitor:
                deletion.Date = UnmonitoredAt.AddDays(-2);
                break;
            case SweepMiss.OtherArtistsHistoryOnly:
                unmonitor!.ArtistId = 99;
                deletion.ArtistId = 99;
                break;
            case SweepMiss.AudioStillInFolder:
                Touch(_singleFolder, "01 Burn the Witch (alt).flac");
                break;
            case SweepMiss.UnrecognizedFileInFolder:
                Touch(_singleFolder, "booklet.pdf");
                break;
        }

        if (unmonitor != null)
        {
            _history.Rows.Add(unmonitor);
        }

        _history.Rows.Add(deletion);

        _cleanup.SweepStrandedFolders(Artist(), new[] { single });

        Assert.True(File.Exists(Path.Combine(_singleFolder, "cover.jpg")));
        Assert.True(File.Exists(Path.Combine(_singleFolder, "01 Burn the Witch.lrc")));
        Assert.Empty(_recycleBin.Deleted);
    }

    [Fact]
    public void SweepStrandedFolders_AlreadyRemovedFolder_IsQuietNoOp()
    {
        Directory.Delete(_singleFolder);
        _history.Rows.Add(PluginUnmonitor(Single().Id, UnmonitoredAt));
        _history.Rows.Add(Deleted(Single().Id, Path.Combine(_singleFolder, "01 Burn the Witch.flac"), UnmonitoredAt.AddSeconds(4)));

        _cleanup.SweepStrandedFolders(Artist(), new[] { Single() });

        Assert.Empty(_recycleBin.Deleted);
        Assert.DoesNotContain(Logs(), l => l.StartsWith("Warn|", StringComparison.Ordinal) || l.StartsWith("Info|", StringComparison.Ordinal));
    }

    // ============================================================
    // Host container
    // ============================================================

    [Fact]
    public void HostContainer_ResolvesFolderCleanupIntoTheCleanupService()
    {
        using var container = new Container(rules => rules.WithNzbDroneRules());
        container.AutoAddServices(new List<Assembly> { typeof(SingleFolderCleanup).Assembly });
        container.RegisterInstance(Stub<IAlbumService>());
        container.RegisterInstance(Stub<ITrackService>());
        container.RegisterInstance(Stub<IMediaFileService>());
        container.RegisterInstance(Stub<IDeleteMediaFiles>());
        container.RegisterInstance(Stub<IHistoryRepository>());
        container.RegisterInstance(Stub<IDiskProvider>());
        container.RegisterInstance(Stub<IRecycleBinProvider>());
        container.RegisterInstance(Stub<NzbDrone.Core.Download.TrackedDownloads.ITrackedDownloadService>());
        container.RegisterInstance(Stub<NzbDrone.Core.History.IHistoryService>());
        container.RegisterInstance(LogManager.CreateNullLogger());

        Assert.IsType<SingleFolderCleanup>(container.Resolve<ISingleFolderCleanup>());
        Assert.IsType<SingleCleanupService>(container.Resolve<ISingleCleanupService>());
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static T Stub<T>()
        where T : class => DispatchProxy.Create<T, UnusedProxy>();

    private static List<Track> TwoDiscTracks() =>
        new() { new Track { MediumNumber = 1 }, new Track { MediumNumber = 2 } };

    private static List<TrackFile> DeletedAudio(string folder, string name) =>
        new() { new TrackFile { Id = 1, Path = Path.Combine(folder, name) } };

    private static void Touch(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(folder, name), "x");
        }
    }

    private Artist Artist() => new() { Id = 7, Name = "PVRIS", Path = _artistPath };

    private static Album Single() => Album(9, "Burn the Witch", "Single", monitored: false);

    private static Album Album(int id, string title, string type, bool monitored) =>
        new() { Id = id, Title = title, AlbumType = type, Monitored = monitored };

    private static EntityHistory PluginUnmonitor(int albumId, DateTime date)
    {
        var history = new EntityHistory { EventType = EntityHistoryEventType.Unknown, ArtistId = 7, AlbumId = albumId, Date = date, SourceTitle = "UniqueSingles" };
        history.Data.Add("Plugin", "UniqueSingles");
        history.Data.Add("Action", "Unmonitor");
        history.Data.Add("Reason", "all tracks redundant");
        return history;
    }

    private static EntityHistory Deleted(int albumId, string path, DateTime date)
    {
        var history = new EntityHistory { EventType = EntityHistoryEventType.TrackFileDeleted, ArtistId = 7, AlbumId = albumId, Date = date, SourceTitle = path };
        history.Data.Add("Reason", nameof(DeleteMediaFileReason.Manual));
        return history;
    }

    private IList<string> Logs() => _logTarget.Logs;

    private List<string> RemovedFolders() => Logs()
        .Where(l => l.Contains("folder removed", StringComparison.Ordinal))
        .Select(l => l.Split("folder='")[1].Split('\'')[0])
        .ToList();

    /// <summary>Lidarr's RecycleBinProvider.DeleteFile semantics: move under subfolder, or delete when unconfigured.</summary>
    private sealed class RecordingRecycleBin : IRecycleBinProvider
    {
        public RecordingRecycleBin(string root) => Root = root;

        public string Root { get; }

        public bool Configured { get; set; } = true;

        public bool Throw { get; set; }

        public List<(string Path, string Subfolder)> Deleted { get; } = new();

        public void DeleteFile(string path, string subfolder = "")
        {
            if (Throw)
            {
                throw new IOException("recycle failed");
            }

            Deleted.Add((path, subfolder));
            if (!Configured)
            {
                File.Delete(path);
                return;
            }

            var destination = Path.Combine(Root, subfolder);
            Directory.CreateDirectory(destination);
            File.Move(path, Path.Combine(destination, Path.GetFileName(path)));
        }

        public void DeleteFolder(string path) => throw new NotSupportedException();

        public void Empty() => throw new NotSupportedException();

        public void Cleanup() => throw new NotSupportedException();
    }

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    public class MediaFileServiceProxy : DispatchProxy
    {
        public List<TrackFile> Tracked { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMediaFileService.GetFilesWithBasePath))
            {
                // Lidarr's MediaFileRepository matches on the path plus one trailing separator.
                var basePath = ((string)args![0]!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Tracked.Where(f => f.Path.StartsWith(basePath, StringComparison.Ordinal)).ToList();
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class TrackServiceProxy : DispatchProxy
    {
        public List<Track> Tracks { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ITrackService.GetTracksByAlbum))
            {
                return Tracks;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    public class HistoryRepositoryProxy : DispatchProxy
    {
        public List<EntityHistory> Rows { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IHistoryRepository.GetByArtist))
            {
                var artistId = (int)args![0]!;
                var eventType = (EntityHistoryEventType?)args[1];
                return Rows
                    .Where(h => h.ArtistId == artistId && (!eventType.HasValue || h.EventType == eventType))
                    .OrderByDescending(h => h.Date)
                    .ToList();
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
