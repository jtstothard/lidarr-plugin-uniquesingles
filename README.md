# Unique Singles

A [Lidarr](https://lidarr.audio) plugin that automatically detects and cleans up redundant singles when the same track already exists on a monitored album or EP.

When Lidarr monitors an artist, it tracks all release types — albums, EPs, and singles. This often leads to duplicate tracks: a single gets downloaded, and then the same track appears on a full album. Unique Singles detects these duplicates and removes the redundant single.

**Example:** You download Chappell Roan's single "HOT TO GO!" and later download the album "The Rise and Fall of a Midwest Princess". Unique Singles detects that "HOT TO GO!" on the single matches the album track, unmonitors the single, and deletes its files.

## Requirements

- **Lidarr nightly branch** — plugin support is not available in the stable release channel.

## Installation

1. Open Lidarr and go to **System → Plugins** (or **Settings → General → Plugins** on some builds)
2. Paste the repository URL:

   ```
   https://github.com/jtstothard/lidarr-plugin-uniquesingles
   ```

3. Click **Install**
4. Lidarr will download the plugin and restart

## Configuration

After installation, add a Unique Singles connection:

1. Go to **Settings → Connect**
2. Click the **+** button to add a new connection
3. Select **Unique Singles** from the list
4. Configure the settings (see below) and click **Save**

### Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Duration tolerance (ms)** | 3000 | Maximum duration difference for title + duration matching. Tracks within ±3 seconds are considered the same recording. |
| **Release types to compare** | `Album, EP` | Comma-separated release types used as comparison sources. The plugin checks whether single tracks appear on these release types. |
| **Title-only match action** | Flag for review | What to do when a single track matches an album track by title alone (no duration match). See matching tiers below. |
| **Exact FLAC analysis (report only)** | Off | Verify unresolved downloaded FLAC tracks locally; report complete duplicates without adding cleanup authority. |
| **Local FFmpeg executable** | `ffmpeg` | Operator-provided decoder inside the Lidarr runtime; no decoder is bundled. |
| **Exact FLAC cleanup** | Off | Separately opt into whole-release cleanup after full verification and source/content revalidation. Also enables analysis. |
| **Eagerly unmonitor redundant singles** | On | Unmonitor a monitored single with no downloaded files as soon as every catalog track is Tier-1/Tier-2 redundant against your downloaded library — at metadata refresh, album add/refresh, and monitoring changes, so it is normally not grabbed, and again on album imports and the scheduled scan. Best effort, not a guarantee: Lidarr can still grab a single in some paths (see [eager unmonitor design](docs/eager-unmonitor.md)); one that downloads anyway is cleaned up by the import-time cleanup (needs the Connect entry) or the scan (off by default), unless you fetched it yourself after the unmonitor or the cleanup cannot establish where the files came from. This setting never deletes files and never touches singles that already have downloads. |
| **Scan interval (minutes)** | 1440 (24 hours) | How often to run an automatic full-library scan. Minimum 60 minutes. Only read from the **UniqueSingles Scan** entry under **Settings → Metadata**; the value on the Connect entry is ignored. |

The same settings form appears in two places. The Connect entry controls import-triggered cleanup, and when it exists the scheduled scan uses its matching settings too. The Metadata entry controls the scan interval and supplies matching settings when no Connect entry is available; if neither supplies matching settings, the scan uses defaults.

See [exact FLAC operations](docs/exact-flac-analysis.md) for supported formats, decoder setup, evidence, limits and [disposable host acceptance](docs/acceptance/issue-42.md). Report-only analysis is not a dry run of existing metadata cleanup.

## Matching Tiers

Unique Singles uses a 3-tier cascade to match single tracks against album/EP tracks:

| Tier | Method | Confidence | Action |
|------|--------|------------|--------|
| **Tier 1** | MusicBrainz Recording MBID match | ~99.9% | Auto-clean (unmonitor + delete) |
| **Tier 2** | Title + duration match (within tolerance) | ~95% | Auto-clean (unmonitor + delete) |
| **Tier 3** | Title-only match | ~80% | Configurable: flag for review, skip, or auto-clean |

**Tier 1** matches are exact — the same MusicBrainz recording appears on both the single and the album. This is the highest confidence match.

**Tier 2** matches use normalized title comparison plus duration within the configured tolerance. Handles cases where MusicBrainz has different recording IDs for the same song (e.g., different mastering).

**Tier 3** matches find the same title but can't confirm duration. By default, these are flagged for manual review rather than auto-deleted. You can change this behavior with the **Title-only match action** setting.

## How It Works

- **Before the grab (eager):** When a single becomes grabbable — added by a metadata refresh (for example after switching an artist's metadata profile to include Singles), manually added, refreshed, or switched to monitored — Unique Singles immediately unmonitors it if it is fully redundant: every catalog track must be Tier-1/Tier-2 redundant against downloaded tracks on your monitored library releases, and the single must have no downloaded files. This normally happens before RSS or a search grabs it, so the redundant single is usually never downloaded, but it is best effort: Lidarr can still search for or grab a single the plugin just unmonitored (for example, a search Lidarr queues after a metadata refresh or when an artist is added). A single with a download already in the download client's queue or in Lidarr's tracked downloads (grabbed, downloading, or awaiting import) is deliberately left monitored, so the import-time cleanup handles its files. The clean-up is conditional: it needs the Connect entry (import) or the scheduled scan (off by default), and it only deletes files that arrived from a download that was already in flight, from an automatic grab (including one Lidarr makes after the unmonitor), or from a grab you started before the unmonitor. Files stay if a delete fails, if the grab provenance cannot be established, if the single is monitored again, or if you fetched the release yourself after the unmonitor (interactive search, a Search you clicked, a manual import without a download): that is your own download, not a leak. If a download you grabbed fails and Lidarr re-searches it automatically, the replacement is cleaned. Eager unmonitors are recorded in Lidarr's History. Can be turned off with **Eagerly unmonitor redundant singles**. Details: [eager unmonitor design](docs/eager-unmonitor.md).
- **On import:** When an album or EP is imported, Unique Singles checks whether any monitored singles contain tracks matching the imported tracks. Matching singles are unmonitored and their files deleted.
- **Scheduled scan:** A full-library scan runs periodically (configurable interval) to catch any singles that were missed by import-triggered cleanup — for example, singles added before the plugin was installed. The scan is off by default. To turn it on, go to **Settings → Metadata**, open **UniqueSingles Scan**, and tick **Enable**. Untick it to stop the scan.

Singles with downloaded files are checked with all three tiers, and redundant ones are unmonitored and their files deleted. Singles without downloaded files are unmonitored (never deleted) when every track has a Tier-1 or Tier-2 match against downloaded tracks on monitored releases of the configured comparison types (Album and EP by default): eagerly (see above), on an album import, and on the scheduled scan, which catches singles that existed before their album was downloaded. With **Eagerly unmonitor redundant singles** off, the import and scan paths fall back to exact MBID (Tier-1) matches only. Otherwise they are left alone; a single whose song is only on an album you haven't downloaded yet stays wanted. Only downloaded tracks on those monitored releases are used as comparison sources. Releases with a Live, Remix, DJ-mix, or Demo secondary type are never used as comparison sources, because their tracks often share a title and length with the studio recording.

Every path applies the same whole-release protection: a single with any catalog track that has no Tier-1/Tier-2 match — downloaded or not — is exclusive content and is never unmonitored or deleted, and title-only (Tier 3) matches never unmonitor a file-less single. See [eager unmonitor design](docs/eager-unmonitor.md) for the hook points and why a grab-time hook is not used.

When Unique Singles deletes all of a single's audio, it also removes what is left of the single's folder, if that is only recognized sidecars: album artwork (`cover.jpg`, `folder.jpg`, ...), `.nfo`, `.lrc`, and OS files like `.DS_Store`. They go to Lidarr's recycle bin next to the deleted audio (or are deleted if no recycle bin is set), and then the folder is removed only if it is empty. Anything else in the folder — untracked audio, music videos, playlists, booklets, cue sheets, rip logs, subfolders, files Lidarr tracks for another release — keeps the folder and everything in it, and the reason is logged. The scan also cleans folders earlier versions left behind, when Lidarr History shows the plugin unmonitored that single and deleted its files. See [single folder cleanup](docs/single-folder-cleanup.md) for the full allowlist, guards and limits.

## Known Issues

- **Requires Lidarr nightly branch.** Plugin support is not available in the stable release channel.
- **Tubifarry coexistence.** If Tubifarry's QueueCleaner is installed alongside UniqueSingles, a plugin dependency conflict may occur. Workaround: disable Tubifarry's QueueCleaner, or use only UniqueSingles for cleanup.
- **Scheduled scan location.** The scan is enabled and configured in **Settings → Metadata** (the **UniqueSingles Scan** entry), but Lidarr lists the scheduled task itself under **System → Tasks → Scheduled** as **Unique Singles Scan**. This is a side effect of Lidarr's limited plugin scheduling API and does not affect functionality.

## Related

- [Lidarr Issue #2671](https://github.com/Lidarr/Lidarr/issues/2671) — Unmonitor singles if on album
- [Lidarr Issue #1025](https://github.com/Lidarr/Lidarr/issues/1025) — Singles included with Albums

## Support

If you find this plugin useful, consider buying me a coffee:

[☕ Ko-fi](https://ko-fi.com/trshpotato)

## License

[MIT](LICENSE)

### Optional exact FLAC reports

Enable **Exact FLAC analysis (report only)** in the matching settings to verify unresolved downloaded singles locally. It defaults off and requires an operator-provided FFmpeg executable in the Lidarr host/container. Complete verified releases are reported without additional cleanup. Existing metadata cleanup still runs. See [decoder, limits and deployment notes](docs/exact-flac-analysis.md).

### Keep and review outcomes

Scans and imports report `keep-exclusive` when any evaluated downloaded track has
no eligible match. This protects the whole release, even if another track has a
title-only match. Track diagnostics remain available, but that release does not
increase the review-needed count. “Exclusive” means unmatched against the current
eligible comparison sources, not proof that the recording exists nowhere else.

With **Tier 3 action** set to **Flag only**, releases whose remaining uncertainty
is title-only matching report `review-needed` (`outcome=unresolved-review`). Each
release counts once, even with several title-only tracks. Logs include candidate
release/track identity, matched source identity, and the reason for keeping or
reviewing the release. Existing cleanup settings, matching tolerance, source
eligibility, and no-file Tier-1 unmonitoring continue to apply.

Enable **Exact FLAC cleanup** separately to permit cleanup after complete exact
coverage and immediate source/content revalidation. It defaults off and also enables
analysis. Analysis alone remains report-only. Missing, changed or unverifiable files
protect the release; every track needs a retained eligible counterpart. See the
[cleanup contract](docs/exact-flac-analysis.md#explicit-exact-cleanup-41).
