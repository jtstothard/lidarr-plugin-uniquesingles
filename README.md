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
| **Scan interval (minutes)** | 1440 (24 hours) | How often to run an automatic full-library scan. Minimum 60 minutes. |

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

- **On import:** When an album or EP is imported, Unique Singles checks whether any monitored singles contain tracks matching the imported tracks. Matching singles are unmonitored and their files deleted.
- **Scheduled scan:** A full-library scan runs periodically (configurable interval) to catch any singles that were missed by import-triggered cleanup — for example, singles added before the plugin was installed.

Only singles with downloaded files are processed. Albums and EPs must be monitored and have files imported to be used as comparison sources.

## Known Issues

- **Requires Lidarr nightly branch.** Plugin support is not available in the stable release channel.
- **Tubifarry coexistence.** If Tubifarry's QueueCleaner is installed alongside UniqueSingles, a plugin dependency conflict may occur. Workaround: disable Tubifarry's QueueCleaner, or use only UniqueSingles for cleanup.
- **Scheduled scan location.** The scheduled scan appears under **Settings → Metadata** (not Tasks) due to Lidarr's limited plugin scheduling API. This is a cosmetic issue and does not affect functionality.

## Related

- [Lidarr Issue #2671](https://github.com/Lidarr/Lidarr/issues/2671) — Unmonitor singles if on album
- [Lidarr Issue #1025](https://github.com/Lidarr/Lidarr/issues/1025) — Singles included with Albums

## License

[MIT](LICENSE)
