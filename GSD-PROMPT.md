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
4. If ALL of a single's tracks match tracks on monitored albums/EPs:
   - DELETE the single's files from disk
   - UNMONITOR the single in Lidarr
   - LOG the action
5. If any track is unique (no match), keep the whole single

MATCHING STRATEGY (3-tier):

Tier 1 — foreignRecordingId match (highest confidence):
  Single track's foreignRecordingId == album track's foreignRecordingId
  → This is the MusicBrainz Recording MBID, already in Lidarr's database
  → No external API calls needed
  → 99.9% confidence. Auto-unmonitor and delete.

Tier 2 — Title + Duration match (high confidence):
  Normalized title match (case-insensitive, strip feat. annotations, normalize punctuation)
  AND duration within ±3 seconds (3000ms)
  → ~95% confidence. Auto-unmonitor and delete.

Tier 3 — Title-only match (flag for review):
  Title matches but recording IDs and/or durations differ significantly
  → ~80% confidence. Log as "REVIEW NEEDED" but do NOT auto-delete.

No match → Keep the single (truly unique standalone release).

MATCHING CRITERIA DETAILS:
- Title: case-insensitive, strip "(feat. Artist)" annotations, normalize trailing punctuation (!?.)
- Duration: within ±3 seconds tolerance (3000ms) for Tier 2
- foreignRecordingId: exact UUID match for Tier 1
- Only check against monitored Albums and EPs (albumType "Album" or "EP")
- Only check against DOWNLOADED albums/EPs (statistics.trackFileCount > 0)
- Only match against tracks that HAVE FILES (track.hasFile == true)
- EPs are treated like albums (check both). This should be configurable for future extension.

CRITICAL EDGE CASES:

1. Multi-track singles: A single can have multiple tracks (e.g., "Bitter" single has "Bitter" + "Die Young (acoustic)"). Only mark as redundant if ALL tracks on the single have matches. If even one track is unique, keep the whole single.

2. Different MBIDs for same song: Sometimes the same song has different MusicBrainz Recording IDs between single and album (e.g., "Pink Pony Club" — different IDs but same duration). Fall back to Tier 2 (title + duration) matching.

3. Remixes/alternate versions: "Good Hurt" vs "Good Hurt (Aevion remix)" have different recording IDs and different titles. These should NOT match. The tier system handles this correctly.

4. Partial downloads: Only match against album/EP tracks where hasFile == true. If the album exists but a specific track failed to import, that track shouldn't count as "on the album."

5. Not-yet-downloaded singles: Only process singles with trackFileCount > 0. No point unmonitoring something that hasn't downloaded yet (Lidarr may still be searching).

6. Featuring annotations: Strip "(feat. Artist)" from titles before comparison. Some releases include featuring in the title, others don't.

KEY CONSTRAINTS:
- Use Lidarr API only (no external API calls, no MusicBrainz API, no database access)
- Must support --dry-run mode (report what WOULD happen without doing it) — this is the DEFAULT
- Must require --process flag to actually make changes (unmonitor/delete)
- Must be idempotent (safe to run multiple times — re-checking already unmonitored singles is harmless)
- Must log all actions clearly (artist, album, single, track, match tier, reason)
- Release types to check should be configurable (default: Album, EP) for future extensibility
- Must handle API errors gracefully (timeouts, missing data, empty responses)

LIDARR API ENDPOINTS:
- GET /api/v1/artist — all monitored artists
- GET /api/v1/album?artistId={id} — all albums for an artist (includes albumType, monitored, statistics)
- GET /api/v1/track?albumId={id} — tracks for an album (includes foreignRecordingId, duration, hasFile, title)
- GET /api/v1/trackfile/{id} — track file details (includes path for deletion)
- PUT /api/v1/album/{id} — update album (set monitored: false to unmonitor)
- DELETE /api/v1/trackfile/{id} — delete track file from disk

DEPLOYMENT:
- Daily cron job at 3am
- Runs against Lidarr at http://192.168.10.34:8686
- API key will be in .env file
- Music library at /mnt/user/media/music/ (on Unraid server)
- SSH access to Unraid: ssh -i ~/.ssh/unraid_ed25519 root@192.168.10.34

EXPECTED OUTPUT:
- Python script: cleanup_redundant_singles.py
- CLI: --dry-run (default), --process (live), --verbose, --artist "Name" (single artist mode)
- Config: .env file for Lidarr URL, API key, paths, matching config
- Logging: structured log with artist/album/single/track/match-tier/reason
- Summary: count of singles checked, redundant found, by tier, deleted, kept, flagged for review

VERIFICATION:
- Dry-run on full library first
- User manually reviews output to confirm accuracy
- Only then run with --process

See EDGE-CASES.md in this repo for real data examples from the Chappell Roan library showing each edge case with actual API responses.
```

---

## How to Use

1. `cd ~/lidarr-unique-singles`
2. Run `/gsd` in your terminal
3. Paste the prompt above
4. GSD will create milestones, slices, and tasks
5. Review the plan before executing
