# Single folder cleanup (#65)

## Problem

Unique Singles deletes a redundant single's track files through Lidarr's deletion service.
Anything else in the single's folder stayed: `album.nfo`, `folder.jpg`/`cover.jpg`, `.lrc`
lyrics. Those files keep the folder non-empty, so Lidarr's own empty-folder cleanup never
removes it, and the library keeps a `Single/(year) Title` folder with no music in it.

`SingleFolderCleanup` (`ISingleFolderCleanup`) removes the recognized sidecars and then the
folder. It started as PR #66 by Jason Krause; this version tightens what may be
removed and adds the sweep for folders stranded before the fix.

## When it runs

- **After a delete:** `SingleCleanupService.TryDeleteSingleFiles` calls `RemoveLeftovers` only
  when every track file of the single was deleted (import cleanup, self-check, scan, exact-audio
  cleanup). A failed or aborted deletion leaves the folder alone.
- **On the scheduled/manual scan:** `ScanArtistWithOptions` calls `SweepStrandedFolders` once
  per artist after the per-single checks, for folders earlier versions left behind (see below). The
  scheduled scan is off by default (**Settings → Metadata → UniqueSingles Scan → Enable**), so
  stranded folders are only swept once it is enabled or a scan command runs.

Every failure is logged at Warn (`reason=folder-cleanup-failed` / `folder-sweep-failed`) and
never changes the cleanup result.

## What counts as a sidecar

Only these files are removed. Matching is on the file name, case-insensitive.

| Kind | Files |
|------|-------|
| Album artwork | `cover`, `folder`, `front`, `albumart`, `albumartsmall`, `discart` with `.jpg`, `.jpeg`, `.png` or `.webp` (Lidarr's Kodi metadata writes `folder.*` and `discart.*`) |
| Release metadata | any `.nfo`, `.lrc`, `.m3u`, `.m3u8` |
| OS folder metadata | `.DS_Store`, `Thumbs.db`, `desktop.ini` |

Everything else keeps the folder, and nothing in it is touched: audio Lidarr doesn't track
(`reason=untracked-audio`), and any other file (`reason=unrecognized-files`), such as music
videos (`.mp4`, `.mkv`), booklets (`.pdf`), other images (`back.jpg`, `scans/`), text files,
other playlist formats, and hidden files. The keep line is logged at Info with up to ten of the
file names, so it is clear why a folder stays.

Deliberately not on the list:

- **`.cue` and `.log`.** Cue sheets and rip logs are rip provenance that some people keep on
  purpose, and a cue sheet can be the only track map of an image rip. With no recycle bin set
  (Lidarr's default) a removal is permanent, so these keep the folder.
- **AppleDouble `._*` files.** They belong to whatever file they shadow; the plugin cannot tell
  whether that file was a sidecar.

## Guards, in order

For each folder:

1. Strictly inside the artist folder (`outside-artist-folder`). A single stored loose in the
   artist folder never causes anything in the artist folder to be removed.
2. The folder still exists (`folder-missing`, Debug).
3. No subfolders (`has-subfolders`). Entries are listed with hidden and system files included;
   Lidarr's `IDiskProvider.GetFiles` skips those.
4. No files Lidarr tracks anywhere under it (`other-tracked-files`, via `GetFilesWithBasePath`).
5. No audio (`untracked-audio`) and nothing outside the allowlist (`unrecognized-files`). Nothing
   is recycled unless every file passes.
6. Each listed sidecar goes to `IRecycleBinProvider.DeleteFile(path, subfolder)` with the subfolder
   Lidarr's `MediaFileDeletionService` uses for audio:
   `GetParentFolder(artist.Path).GetRelativePath(GetParentFolder(file))`. Audio and sidecars from
   one folder land together, e.g. `<recycle bin>/<artist>/Single/(2024) Title/`. Only the files
   listed in step 3 are recycled, by exact path.
7. The folder is removed with a **non-recursive** `Directory.Delete(folder, false)`: the operating
   system's remove-if-empty. If anything appeared after the checks, removal fails, the folder and
   the new content stay, and Info logs `reason=not-empty-at-delete`. `IDiskProvider.DeleteFolder`
   is not used: with `recursive: false` it still calls `EmptyFolder` when top-level files exist,
   which permanently deletes them (bypassing the recycle bin) and force-deletes subfolders.

A removal logs `UniqueSingles folder removed` at Info with the folder, the trigger (`delete` or
`sweep`), and the number of recycled sidecars.

## Multi-folder layouts

Lidarr's multi-disc track format puts each disc in its own folder (`Release/CD 01/`,
`Release/CD 02/`), with album artwork in `Release/`. The folders cleaned are:

- every folder that directly held one of the single's deleted audio files, and
- when there are two or more such folders that are siblings, their shared parent (the release
  folder).

They are processed deepest first, ties in ordinal path order, so the disc folders are gone before
the release folder is checked. Outcomes:

- All disc folders hold only sidecars: disc folders, then the release folder, are removed.
- A disc folder holds anything unrecognized: that disc folder stays, so the release folder fails
  the `has-subfolders` guard before anything in it is recycled. The other disc folders are still
  cleaned.
- Audio in both `Release/` and `Release/CD 02/`: `CD 02` first, then `Release`.
- A release folder above a single disc folder (only one audio folder) is not climbed into: only
  the disc folder is cleaned. No folder above the release folder is ever considered.

## Sweep for folders stranded before the fix

Earlier versions deleted audio and left sidecars, so issue #65's folders already exist. The sweep
only touches a folder it can attribute to a plugin cleanup from Lidarr History:

1. A plugin `Unmonitor` row for the album (event type Unknown, `Plugin=UniqueSingles`,
   `Action=Unmonitor`) written by the History recorder added in #44, and
2. the album is still one of the artist's singles and is still unmonitored (a single monitored
   again is wanted back), and
3. a Lidarr `TrackFileDeleted` row for that album with `Reason=Manual` (what deletion through
   `IDeleteMediaFiles` records) dated from one minute before to one hour after a plugin unmonitor
   of it. The plugin deletes right after it unmonitors; the hour covers slow exact-audio
   revalidation.

The folder is the parent of the deleted file's path (`SourceTitle`). It then goes through exactly
the same guards as above: no audio, only allowlisted sidecars, no tracked files, no subfolders,
non-recursive removal. Each folder is removed once; later scans see `folder-missing` at Debug.

What the sweep cannot clean, and leaves alone:

- **Singles cleaned before #44** (no plugin `Unmonitor` row exists), and singles whose History
  rows were removed (for example by deleting and re-adding the artist). Nothing ties those
  folders to the plugin, and a sidecar-only folder can as well be a single the user deleted by
  hand or a release Lidarr never imported.
- **Folders whose single was monitored again, deleted from Lidarr, or retyped** away from Single.
- **Folders kept by unrecognized content** (the same allowlist applies).

Those folders can be cleaned by hand; the keep lines in the log name them where attribution
succeeded but content did not pass.
