# Lidarr Unique Singles — GSD Kickoff Prompt

Copy the prompt below and paste it into `/gsd` in the `~/lidarr-unique-singles` directory.

---

## Prompt

```
I want to build "Lidarr Unique Singles" — a Python script that automatically identifies and removes redundant singles from my Lidarr library.

PROBLEM:
Lidarr monitors all release types for each artist. When a single's tracks already appear on a monitored album or EP, the single is redundant. Example: "Hot To Go" single is downloaded, but the track is already on "The Rise and Fall of a Midwest Princess" album. Result: duplicate files, wasted downloads, blocklist pollution.

WHAT THE SCRIPT SHOULD DO:
1. Query Lidarr API for all monitored artists
2. For each artist, get all monitored singles AND all monitored albums/EPs
3. For each monitored single, compare its tracks against tracks on albums/EPs
4. If a single's track matches a track on an album/EP (by title, artists, duration ±3s):
   - DELETE the single's files from disk
   - UNMONITOR the single in Lidarr
   - LOG the action
5. If no match found, keep the single (it's a truly unique standalone release)

MATCHING CRITERIA:
- Title: case-insensitive match
- Artists: must match (handles multi-artist tracks)
- Duration: within ±3 seconds tolerance
- Only check against monitored Albums and EPs (not unmonitored, not other singles)
- EPs are treated like albums (check both)

KEY CONSTRAINTS:
- Use Lidarr API only (no direct database access)
- Must support --dry-run mode (report what WOULD happen without doing it)
- Must be idempotent (safe to run multiple times)
- Must log all actions clearly (artist, album, single, track, reason)
- Must handle edge cases: compilations, multi-artist tracks, multiple tracks per single
- Must handle API errors gracefully (rate limits, timeouts, missing data)
- Release types to check should be configurable (default: Album, EP) for future extensibility

DEPLOYMENT:
- Daily cron job at 3am
- Runs against Lidarr at http://192.168.10.34:8686
- Music library at /mnt/user/media/music/
- SSH access: ssh -i ~/.ssh/unraid_ed25519 root@192.168.10.34

LIDARR API ENDPOINTS TO RESEARCH:
- GET /api/v1/artist — all monitored artists
- GET /api/v1/album?artistId={id} — albums for an artist
- GET /api/v1/trackfile?albumId={id} — track files for an album
- PUT /api/v1/album/{id} — update album (unmonitor)
- DELETE /api/v1/trackfile/{id} — delete track file
- GET /api/v1/album/{id} — album details with track list

EXPECTED OUTPUT:
- Python script: cleanup_redundant_singles.py
- CLI: --dry-run (default), --process (live), --verbose
- Config: .env file for Lidarr URL, API key, paths
- Logging: structured log with artist/album/single/track details
- Summary: count of singles checked, redundant found, deleted, kept

VERIFICATION:
- Dry-run on full library first
- User manually reviews output to confirm accuracy
- Only then run with --process

FUTURE CONSIDERATIONS:
- Lidarr has its own metadata server (SkyHook) that caches MusicBrainz data, so a future in-Lidarr implementation would have minimal API overhead
- GitHub issues #2671 and #1025 request this feature natively
- Release type config (Album only vs Album+EP) should be easy to change
```

---

## How to Use

1. `cd ~/lidarr-unique-singles`
2. Run `/gsd` in your terminal
3. Paste the prompt above
4. GSD will create milestones, slices, and tasks
5. Review the plan before executing

## What GSD Will Produce

- **M001**: Research & API exploration (test endpoints, understand data shapes)
- **M002**: Core implementation (matching logic, dry-run, live mode)
- **M003**: Testing & deployment (verify on real library, set up cron)

Each milestone produces verified, tested code with summaries.
