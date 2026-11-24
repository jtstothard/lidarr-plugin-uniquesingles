# Eager pre-grab unmonitoring (#26)

## Problem

Before this feature, Unique Singles only acted **after** a redundant single had been
downloaded and imported (`OnReleaseImport`), plus an optional scheduled scan whose no-file
Tier-1 unmonitor path only ran when a scan ran (off by default, minimum 60 minutes). The
reported churn: switch a metadata profile from Albums to Albums + Singles, Lidarr's refresh
adds every single as monitored, RSS/search grabs the redundant ones, they download, and only
then does the import-time cleanup delete them — wasted bandwidth, disk churn, and history
noise week after week.

## Behavior

`EagerSingleUnmonitorService` subscribes (via Lidarr's `EventAggregator`, which discovers
plugin `IHandle<TEvent>` implementations through the plugin assembly's container
registration) to the album lifecycle points where a single becomes grabbable:

| Hook | Fires when | Notes |
|------|-----------|-------|
| `AlbumInfoRefreshedEvent` | Artist metadata refresh completes, with the newly `Added` albums | The production scenario: profile switches and new-artist adds. Tracks are already inserted and monitored flags already assigned; the event publishes inside the refresh, before any queued search/RSS pass can act. |
| `AlbumAddedEvent` | Manual album add | Tracks usually aren't inserted yet; evaluation fails safe (keep monitored) and the follow-up refresh re-evaluates via `AlbumUpdatedEvent`. |
| `AlbumUpdatedEvent` | Album refresh completes | Covers the manual-add follow-up and keeps evaluation current. |
| `AlbumEditedEvent` | `SetAlbumMonitored` (the UI/API monitoring toggle) and album edits | "Reacting when monitoring is switched on" from the issue. Idempotent: our own unmonitor publishes this event with the persisted unmonitored state, and the re-entrant dispatch terminates at the monitored gate. |

At each point the service runs the **same redundancy decision the cleanup path uses**:
`TrackMatcher.CheckSingle` over **every catalog track** of the single, against the identical
comparison pool (downloaded tracks on monitored releases of the configured comparison types,
excluding Live/Remix/DJ-mix/Demo secondary types, never the candidate itself). The single is
unmonitored only when:

1. the **Eagerly unmonitor redundant singles** setting is enabled (default on — the
   behavior the issue asks for; untick to opt out), and
2. it is a monitored `Single`, and
3. it has **no downloaded track files** (releases with content stay owned by the
   import-time/scheduled cleanup paths, whose unmonitor is followed by file deletion), and
4. **every** catalog track has a Tier-1 (MBID) or Tier-2 (title + duration) match.

Match-tier policy: the eager path accepts Tier-1 **and** Tier-2 evidence. The scan's and
album import's no-file path was originally Tier-1-only; since the follow-up fix it applies the
same Tier-1/Tier-2 rule when this setting is on, because the eager hooks only fire when a single
is added or its monitoring changes, so singles that existed before their album was downloaded
were otherwise never re-evaluated. Tier-2 requires a normalized-title match
plus duration agreement, and the eager action is only ever an unmonitor — reversible, no
file deletion — so the Tier-2 confidence bar is proportionate. Tier-3 (title-only) never
unmonitors, matching Tier-3 FlagOnly semantics.

Protections unchanged from the cleanup path:

- **#17 whole-release protection**: any catalog track without a Tier-1/Tier-2 match —
  downloaded or not (an exclusive undownloaded B-side) — keeps the single monitored.
- Comparison sources are downloaded tracks on monitored comparison-type releases only.
- The unmonitor is written to Lidarr History (`Plugin=UniqueSingles`, `Action=Unmonitor`)
  exactly like cleanup-path unmonitors; a history write failure never affects the decision.
- Fail-safe everywhere: missing tracks, lookup failures, or artist-resolution failures keep
  the single monitored.
- The service takes no command-queue, search, download, or deletion dependencies, so it can
  never trigger a search, grab, or file deletion. Its only view of downloads is the read-only
  `IInFlightReleaseProbe` (below). `SetAlbumMonitored` publishes
  `AlbumEditedEvent(album, album)` whose Lidarr handler only reacts to release-monitoring
  changes, which this call does not make.

## Downloads in flight and the leak backstop

A single that is already grabbed, downloading, or awaiting import has no files yet, so it
looks like any other file-less redundant single. Unmonitoring it would let the download
import into an unmonitored release. Every file-less unmonitor (the eager service, and the
Tier-1/Tier-2 no-files path in the scan and import cleanup) therefore first asks
`LidarrInFlightReleaseProbe`, which reads two things without issuing commands:

- Lidarr's tracked-download cache (what Activity → Queue shows). Any state except Imported,
  Ignored, DownloadFailed and DownloadFailedPending counts as in flight.
- Grabbed History rows newer than 6 hours that are not tracked yet and have no
  DownloadImported/DownloadFailed/DownloadIgnored row. This covers the few seconds between a
  grab and Lidarr's debounced queue refresh, and the empty cache after a restart.

An in-flight single stays monitored (`reason=download-in-flight`), and the import-time
self-check cleans its files once they land. Delay-profile pending releases are not counted:
RSS sync re-checks them against the monitored flag, so unmonitoring cancels them.

Some downloads still get past the unmonitor (a grab dispatched just before it, or a Lidarr
search that ignores the monitored flag). The cleanup paths therefore no longer skip every
unmonitored release. A release whose latest plugin History row is the plugin's own
`Unmonitor` and which now has downloaded files is evaluated under the normal cleanup rules
(Tier-3 action, exact-audio, #17 protection) and its files deleted when they are redundant
(`reason=plugin-unmonitored-has-files`). A release with no plugin row was unmonitored by the
user or by Lidarr and is never touched.

If the user re-monitors a single the plugin unmonitored (the album monitoring toggle or
the album editor, which publish `AlbumEditedEvent`), the eager service records a
`UserRemonitor` History row. From then on neither the eager service nor the cleanup paths
unmonitor that release or delete its files. Lidarr's own bulk monitoring writes
(`AlbumMonitoredService` on artist add) publish no edit event and do not count as the user.

## Why not a grab-time hook (`NotificationBase.OnGrab`)

Lidarr notifications fire after `DownloadService` has already sent the download to the
client; a notification cannot veto a grab, so `OnGrab` cannot *prevent* a download.
Unmonitoring there would also race the download into an unmonitored release, which only the
leak backstop above would then clean. The lifecycle hooks above run strictly
earlier than every grab decision (RSS sync reads monitoring state from the database at
decision time; interactive and queued searches execute only after the refresh/add/monitor
operation that triggers our synchronous handlers has completed).

## Settings

`Eagerly unmonitor redundant singles` follows the existing settings pattern and precedence
(Connect entry first, then the Metadata entry, then defaults). Existing installs pick up
the default (on) because Lidarr's settings reader keeps CLR defaults for fields absent from
the persisted definition. Turning it off restores the pre-#26 behavior completely: no
eager unmonitors, and the import/scan no-file path goes back to exact MBID (Tier-1) matches only.
