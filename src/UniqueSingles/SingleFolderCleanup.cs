using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins;

/// <summary>
/// Removes what is left of a redundant single's folder after its audio files were deleted:
/// sidecar files such as NFO, cover art and lyrics, and then the empty folder itself.
/// </summary>
public interface ISingleFolderCleanup
{
    void RemoveLeftovers(Artist artist, Album single, IReadOnlyCollection<TrackFile> deletedFiles);
}

public sealed class SingleFolderCleanup : ISingleFolderCleanup
{
    private readonly IDiskProvider _diskProvider;
    private readonly IRecycleBinProvider _recycleBinProvider;
    private readonly IMediaFileService _mediaFileService;
    private readonly Logger _logger;

    public SingleFolderCleanup(
        IDiskProvider diskProvider,
        IRecycleBinProvider recycleBinProvider,
        IMediaFileService mediaFileService,
        Logger logger)
    {
        _diskProvider = diskProvider ?? throw new ArgumentNullException(nameof(diskProvider));
        _recycleBinProvider = recycleBinProvider ?? throw new ArgumentNullException(nameof(recycleBinProvider));
        _mediaFileService = mediaFileService ?? throw new ArgumentNullException(nameof(mediaFileService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RemoveLeftovers(Artist artist, Album single, IReadOnlyCollection<TrackFile> deletedFiles)
    {
        if (artist == null || single == null || deletedFiles == null || deletedFiles.Count == 0)
        {
            return;
        }

        var folders = deletedFiles
            .Select(f => f.Path)
            .Where(p => p.IsNotNullOrWhiteSpace())
            .Select(p => p.GetParentPath())
            .Where(p => p.IsNotNullOrWhiteSpace())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var folder in folders)
        {
            try
            {
                RemoveFolder(artist, single, folder);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    ex,
                    "UniqueSingles folder cleanup failure: audio already deleted, leftovers remain. artistId={0} singleId={1} single='{2}' folder='{3}' reason=folder-cleanup-failed",
                    artist.Id,
                    single.Id,
                    single.Title,
                    folder);
            }
        }
    }

    private void RemoveFolder(Artist artist, Album single, string folder)
    {
        // Only a folder strictly inside the artist folder can belong to the single alone.
        // Singles stored loose in the artist folder are left as they are.
        if (artist.Path.IsNullOrWhiteSpace() || !artist.Path.IsParentPath(folder))
        {
            LogSkip(artist, single, folder, "outside-artist-folder");
            return;
        }

        if (!_diskProvider.FolderExists(folder))
        {
            LogSkip(artist, single, folder, "folder-missing");
            return;
        }

        if (_diskProvider.GetDirectories(folder).Any())
        {
            LogSkip(artist, single, folder, "has-subfolders");
            return;
        }

        if (_mediaFileService.GetFilesWithBasePath(folder).Any())
        {
            LogSkip(artist, single, folder, "other-tracked-files");
            return;
        }

        var leftovers = _diskProvider.GetFiles(folder, false).ToList();
        var audioExtensions = MediaFileExtensions.Extensions;
        if (leftovers.Any(f => audioExtensions.Contains(Path.GetExtension(f))))
        {
            LogSkip(artist, single, folder, "untracked-audio");
            return;
        }

        // Same recycle bin subfolder Lidarr uses for the deleted audio, so the whole single
        // ends up in one place: <recycle bin>/<artist>/<release type>/<release folder>.
        var subfolder = _diskProvider.GetParentFolder(artist.Path).GetRelativePath(folder);
        foreach (var file in leftovers)
        {
            _recycleBinProvider.DeleteFile(file, subfolder);
        }

        if (_diskProvider.GetFiles(folder, true).Any())
        {
            LogSkip(artist, single, folder, "not-empty-after-recycle");
            return;
        }

        _diskProvider.DeleteFolder(folder, false);

        _logger.Info(
            "UniqueSingles folder removed: artistId={0} artist='{1}' singleId={2} single='{3}' folder='{4}' recycledLeftovers={5}",
            artist.Id,
            artist.Name,
            single.Id,
            single.Title,
            folder,
            leftovers.Count);
    }

    private void LogSkip(Artist artist, Album single, string folder, string reason)
    {
        _logger.Debug(
            "UniqueSingles folder cleanup skip: artistId={0} singleId={1} single='{2}' folder='{3}' reason={4}",
            artist.Id,
            single.Id,
            single.Title,
            folder,
            reason);
    }
}
