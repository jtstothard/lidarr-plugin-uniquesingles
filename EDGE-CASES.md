# Edge Cases Analysis — Lidarr Unique Singles

Based on real data from your Lidarr library (Chappell Roan test case).

---

## ✅ The Good News: `foreignRecordingId` Is the Key

Lidarr stores **MusicBrainz Recording MBIDs** in the `foreignRecordingId` field on every track. This is the most reliable matching key — it identifies the *specific recording*, not just the song.

```
Album "HOT TO GO!": foreignRecordingId = e619cd9d-ee35-4e37-8ee1-51b992ab95d7
Single "HOT TO GO!": foreignRecordingId = e619cd9d-ee35-4e37-8ee1-51b992ab95d7
→ SAME → Redundant ✅
```

This means **no external API calls needed**. Everything is in Lidarr's own database, queried via its API.

---

## Edge Cases Discovered from Real Data

### 1. 🔴 Multi-Track Singles with Exclusive B-Sides

**Example:** "Bitter" single (ID 8185) — 2 tracks:
- Track 1: "Bitter" (recording `51bd7a30`) — NOT on any album → unique
- Track 2: "Die Young (acoustic)" (recording `60ca4db8`) — NOT on any album → unique

**If we check track 1 ("Biter") and it WAS on an album:**
- ❌ Wrong to delete the whole single — track 2 is exclusive
- ✅ Must check ALL tracks on the single before deciding

**Rule:** Only mark single as redundant if **ALL** its tracks appear on monitored albums/EPs.

**Example 2:** "The Subway / The Giver" single (ID 232572) — 2 tracks:
- Track 1: "The Subway" — has its own standalone single too
- Track 2: "The Giver" — also has standalone single (same `foreignRecordingId`)
- Both tracks available as standalone singles → this 2-track single is redundant
- BUT if user only has one of the standalone singles monitored, the 2-track single has tracks from both

**Rule:** Must check each track individually against all monitored albums/EPs.

---

### 2. 🟡 Different Recording MBIDs, Same Song

**Example:** "Pink Pony Club"
- Album recording: `1f79a002-85d2-424a-9b4d-a1407c8504c5`
- Single recording: `8331bad7-f2fe-4961-a6ef-b0f87bd4e30f`
- Duration: Both 258000ms (identical!)

MusicBrainz treats these as **different recordings** even though they're the same song. Possible reasons:
- Single was mastered differently
- Different edit/version submitted to MusicBrainz
- Data entry inconsistency

**Impact:** `foreignRecordingId` matching alone would MISS this case.

**Mitigation:** Fall back to title + duration matching when `foreignRecordingId` doesn't match.

**Recommended matching priority:**
```
1. foreignRecordingId exact match → redundant (highest confidence)
2. title match (case-insensitive) + duration within ±3s → likely redundant
3. title match only → flag for review (don't auto-delete)
```

---

### 3. 🟡 Remixes and Alternate Versions

**Example:** "Good Hurt" has 3 releases:
- EP "School Nights": "Good Hurt" (recording `877d1dd9`)
- Single "Good Hurt": "Good Hurt" (recording `877d1dd9`) → SAME → redundant ✅
- Single "Good Hurt (Aevion remix)": "Good Hurt (Aevion remix)" (recording `580646ed`) → DIFFERENT → NOT redundant ✅

**This works correctly** with `foreignRecordingId` matching because remixes have different MusicBrainz recordings.

**Also works with title matching** because the remix has "(Aevion remix)" suffix.

---

### 4. 🟡 Same Track Name, Different Recordings (Acoustic/Live)

**Example:** "Die Young" variants:
- EP "School Nights": "Die Young" (recording `91d3eb2a`, duration 225901ms)
- Single "Bitter": "Die Young (acoustic)" (recording `60ca4db8`, duration 245000ms)

These are **different recordings**:
- Different `foreignRecordingId`
- Different title (has "(acoustic)" suffix)
- Different duration (225s vs 245s)

**Works correctly** — neither title nor recording ID matches, so both kept.

---

### 5. 🟡 EP/Single Name Collision

**Example:** "School Nights" exists as BOTH:
- EP "School Nights" (ID 8184) — 5 tracks, albumType "EP"
- Single "School Nights" (ID 8186) — 1 track, albumType "Single"

The single track "School Nights" (recording `05bf6409`) does NOT appear on the EP (EP has "Die Young", "Good Hurt", "Meantime", "Sugar High", "Bad for You").

**So the single is NOT redundant** — it's a unique track not on the EP.

**Works correctly** — no recording ID match, no title match against EP tracks.

---

### 6. 🟡 Album Not Yet Downloaded

**Example:** Album monitored but `trackFileCount == 0` (not downloaded yet).

**Rule:** User specified "only albums in library downloaded/imported should be considered."

**Implementation:** Check `statistics.trackFileCount > 0` before using an album/EP for matching.

This means a single won't be flagged as redundant until the album actually downloads. Good — avoids premature deletion.

---

### 7. 🟡 Partially Downloaded Albums

**Example:** Album has 14 tracks but only 10 imported (`trackFileCount: 10`).

**Risk:** Single track might not actually be in the library even though the album entry exists.

**Mitigation:** Check individual track's `hasFile` field:
```json
{
  "hasFile": true,    // ← Only count as "exists" if true
  "trackFileId": 1234
}
```

**Rule:** Only consider a track as "on the album" if `hasFile == true` for that specific track.

---

### 8. 🟢 Duplicate Singles (Same Track, Multiple Single Releases)

**Example:** "The Giver" appears on:
- Standalone single "The Giver" (ID 10294) — recording `57887697`
- 2-track single "The Subway / The Giver" (ID 232572) — recording `57887697` (SAME)

Both are singles. We only check against albums/EPs, so neither would be flagged as redundant by the album check.

**But:** If "The Giver" later appears on a monitored album, BOTH singles would be flagged.
The script should handle this correctly — it unmonitors each single independently.

---

### 9. 🟢 Multi-Artist / Featuring Tracks

**From the API data:** Tracks have `artistId` at the track level, which matches the owning artist.

**Risk:** A track featuring another artist might have different title formatting:
- Album: "Parting Gift" (feat. Brendan Kelly)
- Single: "Parting Gift"

**Mitigation:** Title matching should strip featuring annotations for comparison:
```python
import re
clean_title = re.sub(r'\s*\(feat\..*?\)', '', title)
clean_title = re.sub(r'\s*\(featuring\s.*?\)', '', title, flags=re.IGNORECASE)
```

---

### 10. 🟢 Case and Punctuation Differences

**Example:** Album has "HOT TO GO!" (all caps + exclamation), single might have "Hot to Go!" (different casing).

**Mitigation:** Case-insensitive title matching + normalize punctuation:
```python
import unicodedata
def normalize_title(title):
    # Remove extra spaces, punctuation variations
    title = title.lower().strip()
    title = re.sub(r'[!?.]+$', '', title)  # Strip trailing punctuation
    title = re.sub(r'\s+', ' ', title)     # Normalize spaces
    return title
```

---

### 11. 🔴 Duration Discrepancies Between Sources

**From the data:**
- Album track "HOT TO GO!": `duration: 185000` (185.0s)
- Single track "HOT TO GO!": `duration: 184000` (184.0s)
- Same recording ID, but 1 second different!

This is why duration tolerance is important. ±3 seconds is reasonable.

**But:** Some tracks may have larger differences:
- Radio edit vs album version: Could differ by 30-60+ seconds
- Different mastering: Usually within ±5 seconds

**Rule:** ±3 seconds for "same recording" confidence. Larger differences should NOT auto-match.

---

### 12. 🟡 Single That's Not Downloaded Yet

**Example:** "Good Hurt (Siege remix)" (ID 8198) — `trackFileCount: 0`, not downloaded.

**Rule:** Skip singles that haven't been downloaded yet. Only process singles with `trackFileCount > 0`.

**Why:** No files to delete, and Lidarr may still be trying to download it.

---

## Summary of Matching Strategy

**Tier 1: Recording MBID match (highest confidence)**
```python
single_track["foreignRecordingId"] == album_track["foreignRecordingId"]
```
→ 99.9% confident it's the same recording. Auto-safe to unmonitor.

**Tier 2: Title + Duration match (high confidence)**
```python
normalize(single_title) == normalize(album_title) and
abs(single_duration - album_duration) <= 3000  # ±3 seconds
```
→ ~95% confident. Safe to auto-unmonitor.

**Tier 3: Title-only match (low confidence)**
```python
normalize(single_title) == normalize(album_title)
```
→ ~80% confident. Flag for review, don't auto-delete.

**Tier 4: No match**
→ Keep single (truly unique).

---

## Recommended Configuration (from the prompt)

```python
MATCHING_CONFIG = {
    "release_types_to_check": ["Album", "EP"],  # Extensible
    "require_downloaded": True,                  # Only check albums with files
    "require_has_file": True,                    # Only match tracks with files
    "duration_tolerance_ms": 3000,               # ±3 seconds
    "match_tiers": {
        "tier1": "foreignRecordingId",           # Exact MBID
        "tier2": "title + duration",             # Fuzzy
        "tier3": "title only",                   # Flag only
    },
    "action_by_tier": {
        "tier1": "unmonitor_and_delete",
        "tier2": "unmonitor_and_delete",
        "tier3": "flag_only",                    # Don't auto-delete
    },
    "require_all_tracks_redundant": True,        # Multi-track singles
}
```

---

## Edge Cases to Update in GSD-PROMPT.md

The following edge cases should be explicitly addressed in the implementation:

1. **Multi-track singles**: Only unmonitor if ALL tracks on the single appear on albums/EPs
2. **Different MBIDs, same song**: Fall back to title + duration when MBIDs don't match
3. **Partial downloads**: Only match against tracks where `hasFile == true`
4. **Not-yet-downloaded**: Only process singles where `trackFileCount > 0`
5. **Featuring annotations**: Strip "(feat. X)" for title comparison
6. **Duration tolerance**: ±3 seconds for Tier 2 matching
7. **Tier 3 (title-only)**: Don't auto-delete, flag for manual review
