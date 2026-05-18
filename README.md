# Lidarr Unique Singles

Automatically unmonitor and delete redundant singles that appear on monitored albums or EPs.

## Problem

Lidarr monitors all release types (albums, EPs, singles) for each artist. This causes:
- **Redundant downloads**: Singles downloaded even though track is on an album
- **Blocklist pollution**: Singles fail to import (wrong release group)
- **Queue bloat**: Stuck singles clogging download queue
- **Manual overhead**: Manually reviewing which singles to keep

**Example:**
- Artist: Chappell Roan
- Single: "Hot To Go" → Downloaded
- Album: "The Rise and Fall of a Midwest Princess" → Downloaded
- Result: Track appears twice, single is redundant

## Solution

**Daily cleanup script that:**

1. Scans all monitored artists
2. For each monitored single:
   - Extracts track information (title, artists, duration)
   - Checks if same track exists on any monitored **Album or EP**
   - If match found:
     - **Unmonitors** the single (prevents future downloads)
     - **Deletes** the single files from disk (cleanup)
     - Logs the action
   - If no match:
     - Keeps single monitored (truly unique)

## Logic

**Track matching criteria:**
- Title match (case-insensitive)
- Artist match (all artists for compilation tracks)
- Duration match (within ±3 seconds tolerance)
- Monitored status: Album or EP must be **monitored** in Lidarr

**What gets checked:**
- ✅ Monitored Albums (primary type = "Album", status = "Official")
- ✅ Monitored EPs (primary type = "EP", status = "Official")
- ❌ Unmonitored albums/EPs (ignored)
- ❌ Singles (only check against albums/EPs)

## Requirements

- Python 3.8+
- Access to Lidarr API (http://192.168.10.34:8686)
- Write access to `/mnt/user/media/music/` (for deletion)
- Lidarr API key

## Usage

```bash
# Dry run (no changes)
python3 cleanup_redundant_singles.py --dry-run

# Live run (actually deletes/unmonitors)
python3 cleanup_redundant_singles.py --process

# Monitor mode (scan only, report stats)
python3 cleanup_redundant_singles.py --monitor
```

## Deployment

```bash
# Add to crontab (daily at 3am)
0 3 * * * /usr/bin/python3 /path/to/lidarr-unique-singles/cleanup_redundant_singles.py --process >> /var/log/lidarr-unique-singles.log 2>&1
```

## Future Enhancements

- [ ] Configurable release types (Album only, Album+EP, all)
- [ ] Duration tolerance configuration
- [ ] Artist matching fuzzy matching
- [ ] Manual review mode (interactive)
- [ ] Undo functionality (restore deleted singles)
- [ ] Web UI integration

## License

MIT

## Related

- [Lidarr Issue #2671](https://github.com/Lidarr/Lidarr/issues/2671) - Unmonitor singles if on album
- [Lidarr Issue #1025](https://github.com/Lidarr/Lidarr/issues/1025) - Singles included with Albums
- [Servarr Wiki: Metadata Profiles](https://wiki.servarr.com/lidarr/faq)