# PROJECT: Lidarr Unique Singles

## Vision

Automated cleanup system for Lidarr that identifies redundant singles (tracks that already appear on monitored albums or EPs), unmonitors them, and deletes the files. Reduces queue bloat, blocklist pollution, and manual overhead.

## Problem Statement

Lidarr monitors all release types (albums, EPs, singles) for each artist. This causes:

1. **Redundant downloads**: Singles downloaded even though the track is on a monitored album
2. **Blocklist pollution**: Singles fail to import because they match the wrong release group (single vs album)
3. **Queue bloat**: Stuck single downloads clog the download queue
4. **Manual overhead**: Users must manually review and unmonitor redundant singles

**Example scenario:**
- Artist: Chappell Roan
- Single: "Hot To Go" → Lidarr searches and downloads
- Album: "The Rise and Fall of a Midwest Princess" → Lidarr searches and downloads
- Result: "Hot To Go" appears twice, single is redundant
- Expected: Only download "Hot To Go" once via the album, skip the single

## Solution

**Daily cleanup script that:**

1. Scans all monitored artists in Lidarr
2. For each monitored single:
   - Extracts track information (title, artists, duration)
   - Queries Lidarr for all monitored Albums and EPs
   - Checks each album/EP for matching tracks
3. If match found:
   - **Unmonitors** the single (prevents future downloads)
   - **Deletes** the single files from library (cleanup)
   - Logs the action with reason
4. If no match found:
   - Keeps single monitored (truly unique single)

**Track matching criteria:**
- Title match (case-insensitive)
- Artist match (all artists for compilation tracks)
- Duration match (within ±3 seconds tolerance)
- Release type: Album or EP only (not Singles)
- Monitoring status: Must be **monitored** in Lidarr

## Scope

**In scope (Phase 1):**
- Daily automated scan
- Check against monitored Albums and EPs only
- Match by title + artists + duration
- Unmonitor redundant singles
- Delete redundant single files
- Dry-run mode for testing
- Logging to file

**Out of scope (Phase 1):**
- Unmonitored albums/EPs (ignore these)
- Live albums, remixes, compilations (unless monitored)
- Radio edits, alternate versions (different duration)
- Manual review/interactive mode
- Undo/restore functionality
- Web UI integration

**Future phases may include:**
- Configurable release types (Album only vs Album+EP vs all)
- Duration tolerance configuration
- Fuzzy matching options (typos, alternate spellings)
- Interactive review mode
- Undo functionality
- Lidarr plugin for real-time prevention (at request phase)

## Success Criteria

**Functional:**
- ✅ Script identifies redundant singles with >95% accuracy
- ✅ Dry-run mode reports actions without executing
- ✅ Live run correctly unmonitors redundant singles
- ✅ Live run correctly deletes redundant single files
- ✅ Script handles edge cases (compilations, multi-artist tracks)
- ✅ Logs all actions with artist/album/single details

**Performance:**
- ✅ Scan completes in <5 minutes for typical library (50 artists)
- ✅ No meaningful impact on Lidarr performance
- ✅ Rate limit compliance for Lidarr API

**Reliability:**
- ✅ Script runs daily without errors
- ✅ Handles Lidarr API errors gracefully
- ✅ Logs errors for troubleshooting
- ✅ Idempotent (can be run multiple times safely)

**User experience:**
- ✅ Clear logging output
- ✅ Easy to understand what happened
- ✅ Can restore if needed (through manual re-monitor)
- ✅ Minimal false positives (<5%)

## Non-Goals

- Not a replacement for manual import decisions
- Not a way to force import of failing singles
- Not a full library manager (only handles single/album redundancy)
- Not a real-time prevention system (pre-request filtering is future work)

## Tech Stack

- **Language**: Python 3.8+
- **Libraries**:
  - `requests` - Lidarr API calls
  - `click` - CLI argument parsing
  - `python-dotenv` - Configuration management
  - `logging` - Structured logging
- **Configuration**: `.env` file for Lidarr URL, API key, paths
- **Deployment**: Cron job (daily execution)
- **Testing**: Dry-run mode, unit tests, integration tests

## Key Risks

| Risk | Mitigation |
|------|------------|
| False positive (single actually unique) | Dry-run mode first; monitor logs before live; duration tolerance conservative (±3s) |
| Deletion of wrong files | Only delete singles identified as redundant; dry-run review; manual re-monitor if mistake |
| Lidarr API rate limiting | Cache artist/album data; batch API calls; handle 429 errors gracefully |
| Database locking during deletion | Check if files exist before deletion; handle permission errors; log failures |
| Network failures (Lidarr unreachable) | Retry logic; fail gracefully; log errors; retry next day |

## Constraints

- Must work with existing Lidarr instance (no Lidarr code changes)
- Must work via API only (no database access)
- Must be idempotent (can be run multiple times safely)
- Must support dry-run mode for testing
- Must log all actions clearly
- Must handle rate limits (Lidarr API, network)

## Dependencies

- **External**: Lidarr API (http://192.168.10.34:8686)
- **External**: Filesystem access to `/mnt/user/media/music/`
- **Python**: requests, click, python-dotenv (standard library available)
- **Deployment**: cron (or systemd timer)

## Assumptions

- Lidarr API is accessible and API key is valid
- User has write access to `/mnt/user/media/music/`
- Cron/systemd is available for scheduled execution
- Lidarr library follows standard directory structure (`/music/{Artist}/{Album}/`)
- MusicBrainz duration data is reasonably accurate (±3s tolerance sufficient)
- Monitored Albums/EPs are correctly identified in Lidarr

## Context

This is a workaround for a feature that doesn't exist in Lidarr. Long-term, this could be implemented as:
1. Lidarr plugin (Tubifarry-style) for real-time prevention
2. Core Lidarr feature request (Issues #2671, #1025)
3. Integration with Lidarr's SkyHook metadata server (would reduce API overhead)

For now, this script provides the same functionality with daily scanning instead of real-time prevention.