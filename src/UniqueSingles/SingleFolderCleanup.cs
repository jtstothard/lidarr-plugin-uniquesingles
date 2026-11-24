using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Removes what is left of a redundant single's folder after its audio files were deleted:
/// recognized sidecar files (cover art, NFO, lyrics, playlists, OS metadata) and then the empty
/// folder itself. See docs/single-folder-cleanup.md for the allowlist, the guards, and what the
/// stranded-folder sweep can and cannot attribute.
/// </summary>
public interface ISingleFolderCleanup
{
    /// <summary>
    /// Cleans the folders that held <paramref name="deletedFiles"/> right after every audio file
    /// of <paramref name="single"/> was deleted.
    /// </summary>
    void RemoveLeftovers(Artist artist, Album single, IReadOnlyCollection<TrackFile> deletedFiles);

    /// <summary>
    /// Cleans folders stranded by earlier plugin versions, which deleted a single's audio but
    /// left its sidecars behind. Only folders attributable to a plugin cleanup through Lidarr
    /// History are considered.
    /// </summary>
    void SweepStrandedFolders(Artist artist, IReadOnlyCollection<Album> albums);
}

public sealed class SingleFolderCleanup : ISingleFolderCleanup
{
    /// <summary>
    /// A plugin deletion follows its unmonitor within the same cleanup call. Exact-audio
    /// revalidation can take a while on large files, so allow an hour; a TrackFileDeleted row
    /// outside this window is not attributed to the plugin.
    /// </summary>
    internal static readonly TimeSpan AttributionWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan AttributionClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>File names (any of <see cref="ImageExtensions"/>) that are album artwork.</summary>
    private static readonly HashSet<string> ArtworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cover",
        "folder",
        "front",
        "albumart",
        "albumartsmall",
        "discart",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
    };

    /// <summary>Extensions that are metadata about the release, whatever the file is called.</summary>
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo",
        ".lrc",
        ".m3u",
        ".m3u8",
    };

    /// <summary>Folder metadata the operating system writes on its own.</summary>
    private static readonly HashSet<string> OsMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
    };

    private static readonly EnumerationOptions AllEntries = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
    };

    private readonly IDiskProvider _diskProvider;
    private readonly IRecycleBinProvider _recycleBinProvider;
    private readonly IMediaFileService _mediaFileService;
    private readonly IHistoryRepository _history;
    private readonly Logger _logger;

    public SingleFolderCleanup(
        IDiskProvider diskProvider,
        IRecycleBinProvider recycleBinProvider,
        IMediaFileService mediaFileService,
        IHistoryRepository history,
        Logger logger)
    {
        _diskProvider = diskProvider ?? throw new ArgumentNullException(nameof(diskProvider));
        _recycleBinProvider = recycleBinProvider ?? throw new ArgumentNullException(nameof(recycleBinProvider));
        _mediaFileService = mediaFileService ?? throw new ArgumentNullException(nameof(mediaFileService));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Test seam: runs after the last check and the sidecar recycling, immediately before the
    /// folder removal, so tests can put a file into the folder inside that window.
    /// </summary>
    internal Action<string>? BeforeFolderDelete { get; set; }

    /// <summary>
    /// Whether a leftover file is a sidecar that is safe to remove with the single. Deliberately
    /// narrow: anything not listed here (music videos, booklets, cue sheets, rip logs, audio,
    /// user files) keeps the whole folder.
    /// </summary>
    internal static bool IsRecognizedSidecar(string path)
    {
        var name = Path.GetFileName(path);
        if (OsMetadataNames.Contains(name))
        {
            return true;
        }

        var extension = Path.GetExtension(name);
        if (SidecarExtensions.Contains(extension))
        {
            return true;
        }

        return ImageExtensions.Contains(extension) && ArtworkNames.Contains(Path.GetFileNameWithoutExtension(name));
    }

    public void RemoveLeftovers(Artist artist, Album single, IReadOnlyCollection<TrackFile> deletedFiles)
    {
        if (artist == null || single == null || deletedFiles == null || deletedFiles.Count == 0)
        {
            return;
        }

        CleanFolders(artist, single, deletedFiles.Select(f => f.Path), "delete");
    }

    public void SweepStrandedFolders(Artist artist, IReadOnlyCollection<Album> albums)
    {
        if (artist == null || albums == null || artist.Path.IsNullOrWhiteSpace())
        {
            return;
        }

        // 1. Singles the plugin unmonitored (its own History audit rows, written since #44).
        var unmonitoredAt = _history.GetByArtist(artist.Id, EntityHistoryEventType.Unknown)
            .Where(IsPluginUnmonitor)
            .GroupBy(h => h.AlbumId)
            .ToDictionary(g => g.Key, g => g.Select(h => h.Date).ToList());

        // 2. ...that are still singles in this artist and still unmonitored. A single the user
        //    monitored again is wanted back, so its folder is left alone.
        var singles = albums
            .Where(a => a != null && IsSingle(a) && !a.Monitored && unmonitoredAt.ContainsKey(a.Id))
            .ToDictionary(a => a.Id);
        if (singles.Count == 0)
        {
            return;
        }

        // 3. ...whose track files were deleted (Lidarr's own TrackFileDeleted rows, reason Manual,
        //    which is what the plugin's deletion produces) within the window after the unmonitor.
        //    SourceTitle is the deleted file's full path, so its parent is the folder it lived in.
        var deletedPaths = _history.GetByArtist(artist.Id, EntityHistoryEventType.TrackFileDeleted)
            .Where(h => singles.ContainsKey(h.AlbumId)
                && h.SourceTitle.IsNotNullOrWhiteSpace()
                && h.Data != null
                && h.Data.TryGetValue("Reason", out var reason)
                && string.Equals(reason, nameof(DeleteMediaFileReason.Manual), StringComparison.Ordinal)
                && unmonitoredAt[h.AlbumId].Any(u => h.Date >= u - AttributionClockSkew && h.Date <= u + AttributionWindow))
            .GroupBy(h => h.AlbumId)
            .OrderBy(g => g.Key);

        foreach (var group in deletedPaths)
        {
            CleanFolders(artist, singles[group.Key], group.Select(h => h.SourceTitle), "sweep");
        }
    }

    private void CleanFolders(Artist artist, Album single, IEnumerable<string> deletedAudioPaths, string trigger)
    {
        List<string> folders;
        try
        {
            folders = FoldersToClean(deletedAudioPaths);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                ex,
                "UniqueSingles folder cleanup failure: artistId={0} singleId={1} single='{2}' trigger={3} reason=folder-cleanup-failed",
                artist.Id,
                single.Id,
                single.Title,
                trigger);
            return;
        }

        foreach (var folder in folders)
        {
            try
            {
                CleanFolder(artist, single, folder, trigger);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "UniqueSingles folder cleanup failure: audio already deleted, folder kept. artistId={0} singleId={1} single='{2}' folder='{3}' trigger={4} reason=folder-cleanup-failed",
                    artist.Id,
                    single.Id,
                    single.Title,
                    folder,
                    trigger);
            }
        }
    }

    /// <summary>
    /// The folders that held the single's audio, deepest first so an inner disc folder
    /// (Release/CD1) is gone before its parent is checked. When the audio sat in two or more
    /// sibling folders (Release/CD1, Release/CD2), their shared parent is the release folder and
    /// is cleaned last. Nothing else above an audio folder is ever touched.
    /// </summary>
    private static List<string> FoldersToClean(IEnumerable<string> deletedAudioPaths)
    {
        var comparer = StringComparer.FromComparison(DiskProviderBase.PathStringComparison);
        var folders = deletedAudioPaths
            .Where(p => p.IsNotNullOrWhiteSpace())
            .Select(p => p.GetParentPath())
            .Where(p => p.IsNotNullOrWhiteSpace())
            .Select(p => p!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(comparer)
            .ToList();

        if (folders.Count >= 2)
        {
            var parents = folders.Select(f => f.GetParentPath()).Distinct(comparer).ToList();
            if (parents.Count == 1 && parents[0].IsNotNullOrWhiteSpace() && !folders.Contains(parents[0]!, comparer))
            {
                folders.Add(parents[0]!);
            }
        }

        return folders
            .OrderByDescending(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private void CleanFolder(Artist artist, Album single, string folder, string trigger)
    {
        // Only a folder strictly inside the artist folder can belong to the single alone.
        // Singles stored loose in the artist folder are left as they are.
        if (artist.Path.IsNullOrWhiteSpace() || !artist.Path.IsParentPath(folder))
        {
            LogKeep(artist, single, folder, trigger, "outside-artist-folder");
            return;
        }

        if (!Directory.Exists(folder))
        {
            LogKeep(artist, single, folder, trigger, "folder-missing");
            return;
        }

        // Every entry, hidden and system files included: Lidarr's IDiskProvider.GetFiles skips
        // them, and a hidden user file must keep the folder like any other.
        var entries = Directory.EnumerateFileSystemEntries(folder, "*", AllEntries).ToList();
        if (entries.Any(Directory.Exists))
        {
            LogKeep(artist, single, folder, trigger, "has-subfolders");
            return;
        }

        if (_mediaFileService.GetFilesWithBasePath(folder).Any())
        {
            LogKeep(artist, single, folder, trigger, "other-tracked-files");
            return;
        }

        var audioExtensions = MediaFileExtensions.Extensions;
        var audio = entries.Where(f => audioExtensions.Contains(Path.GetExtension(f))).ToList();
        if (audio.Count > 0)
        {
            LogKeep(artist, single, folder, trigger, "untracked-audio", audio);
            return;
        }

        var unrecognized = entries.Where(f => !IsRecognizedSidecar(f)).ToList();
        if (unrecognized.Count > 0)
        {
            LogKeep(artist, single, folder, trigger, "unrecognized-files", unrecognized);
            return;
        }

        // Same recycle bin subfolder Lidarr's MediaFileDeletionService uses for the deleted
        // audio (root folder → file's folder), so a single's audio and sidecars land together.
        var rootFolder = _diskProvider.GetParentFolder(artist.Path);
        foreach (var file in entries)
        {
            var subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(file));
            _recycleBinProvider.DeleteFile(file, subfolder);
        }

        BeforeFolderDelete?.Invoke(folder);

        // Non-recursive removal is the operating system's remove-if-empty: it fails rather than
        // deleting anything that appeared after the checks above. IDiskProvider.DeleteFolder is
        // not used because it empties the folder first, permanently deleting whatever is there.
        try
        {
            Directory.Delete(folder, false);
        }
        catch (DirectoryNotFoundException)
        {
            LogKeep(artist, single, folder, trigger, "folder-missing");
            return;
        }
        catch (IOException ex)
        {
            _logger.Info(
                "UniqueSingles folder cleanup keep: folder was not empty at removal, left in place. artistId={0} singleId={1} single='{2}' folder='{3}' trigger={4} recycledSidecars={5} reason=not-empty-at-delete detail='{6}'",
                artist.Id,
                single.Id,
                single.Title,
                folder,
                trigger,
                entries.Count,
                ex.Message);
            return;
        }

        _logger.Info(
            "UniqueSingles folder removed: artistId={0} artist='{1}' singleId={2} single='{3}' folder='{4}' trigger={5} recycledSidecars={6}",
            artist.Id,
            artist.Name,
            single.Id,
            single.Title,
            folder,
            trigger,
            entries.Count);
    }

    private void LogKeep(Artist artist, Album single, string folder, string trigger, string reason, IReadOnlyCollection<string>? files = null)
    {
        if (files == null)
        {
            _logger.Debug(
                "UniqueSingles folder cleanup keep: artistId={0} singleId={1} single='{2}' folder='{3}' trigger={4} reason={5}",
                artist.Id,
                single.Id,
                single.Title,
                folder,
                trigger,
                reason);
            return;
        }

        // Content the plugin will not remove is worth seeing at Info: it explains why the folder stays.
        _logger.Info(
            "UniqueSingles folder cleanup keep: folder holds files that are not recognized sidecars, nothing removed. artistId={0} singleId={1} single='{2}' folder='{3}' trigger={4} reason={5} files='{6}'",
            artist.Id,
            single.Id,
            single.Title,
            folder,
            trigger,
            reason,
            string.Join("', '", files.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Take(10)));
    }

    private static bool IsPluginUnmonitor(EntityHistory history)
    {
        return history?.Data != null
            && history.Data.TryGetValue("Plugin", out var plugin)
            && string.Equals(plugin, "UniqueSingles", StringComparison.Ordinal)
            && history.Data.TryGetValue("Action", out var action)
            && string.Equals(action, "Unmonitor", StringComparison.Ordinal);
    }

    private static bool IsSingle(Album album)
    {
        return string.Equals(album.AlbumType, "Single", StringComparison.OrdinalIgnoreCase);
    }
}
