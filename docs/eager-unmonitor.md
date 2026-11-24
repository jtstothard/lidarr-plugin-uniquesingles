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
| `AlbumInfoRefreshedEvent` | Artist metadata refresh completes, with the newly `Added` albums | The production scenario: profile switches and new-artist adds. Tracks are already inserted and monitored flags already assigned. The event carries the refresh's in-memory album instances, not a fresh read, which matters for the ordering caveats below. |
| `AlbumAddedEvent` | Manual album add | Tracks usually aren't inserted yet; evaluation fails safe (keep monitored) and the follow-up refresh re-evaluates via `AlbumUpdatedEvent`. |
| `AlbumUpdatedEvent` | Album refresh completes | Covers the manual-add follow-up and keeps evaluation current. |
| `AlbumEditedEvent` | `SetAlbumMonitored` (the UI/API monitoring toggle) and album edits | "Reacting when monitoring is switched on" from the issue. Idempotent: our own unmonitor publishes this event with the persisted unmonitored state, and the re-entrant dispatch terminates at the monitored gate. |

At each point the service runs the **same redundancy decision the cleanup path uses**:
`TrackMatcher.CheckSingle` over **every catalog track** of the single, against the identical
comparison pool (downloaded tracks on monitored releases of the configured comparison types,
excluding Live/Remix/DJ-mix/Demo and unreadable-secondary-type releases, treating releases with any other secondary type as Tier-1-only sources, never the candidate itself). A single that itself carries Live/Remix/DJ-mix/Demo, or whose secondary types are unreadable, is never unmonitored. The single is
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
  exactly like cleanup-path unmonitors. The row is written *before* the unmonitor, because the
  leak backstop (below) depends on it: if it cannot be written the single stays monitored
  (`reason=unmonitor-history-write-failed`) and the next event or scan retries. If the
  unmonitor itself then fails, an `UnmonitorReverted` row supersedes the `Unmonitor` row.
- Fail-safe everywhere: missing tracks, lookup failures, or artist-resolution failures keep
  the single monitored.
- The service takes no command-queue, search, download, or deletion dependencies, so it can
  never trigger a search, grab, or file deletion. Its only view of downloads is the read-only
  `IInFlightReleaseProbe` (below). `SetAlbumMonitored` publishes
  `AlbumEditedEvent(album, album)` whose Lidarr handler only reacts to release-monitoring
  changes, which this call does not make.

## What the unmonitor guarantees, and what it does not

The handlers run synchronously inside Lidarr's album lifecycle operations, but that does
**not** make the unmonitor happen strictly before every grab decision, and a search queued by
Lidarr does not wait for it. Checked against the Lidarr source at tag `v3.1.3.4968`
(github.com/Lidarr/Lidarr@v3.1.3.4968):

- The metadata-refresh event hands handlers the refresh's in-memory `Added` album instances
  (`RefreshEntityServiceBase` passes `sortedChildren.Added` to `PublishChildrenUpdatedEvent`).
  `AlbumAddedService` reads the `Monitored` flag on those instances, not the database, and
  caches the ids of the monitored ones. The plugin writes the database row and updates the
  instance it was handed, so whether Lidarr's own handler sees the unmonitor depends on
  handler order and on whether it holds a different, stale copy of the album. On the
  metadata-profile-switch path the plugin's unmonitor can miss the instances Lidarr goes on
  to search.
- `AlbumAddedService.SearchForRecentlyAdded` then pushes an `AlbumSearchCommand` for those
  cached ids. Nothing on that path re-checks the monitored flag, and
  `MonitoredAlbumSpecification` explicitly skips its monitored check during a search, so a
  single unmonitored in between can still be grabbed. That grab is recorded with
  `ReleaseSource=Search`, so the leak backstop (below) treats it as automatic and cleans the
  files once they import, when a cleanup path is on.
- When an artist is added, `ArtistScannedHandler` calls
  `AlbumMonitoredService.SetAlbumMonitoredStatus`, which applies the add-time monitor option
  (for example All, or Missing, which monitors released albums without files) and writes the
  result with `UpdateMany`. That can re-monitor a single the plugin had just unmonitored, and it
  publishes no `AlbumEditedEvent`, so the plugin is not told. The single is then monitored
  while the plugin's latest row for it is an `Unmonitor`, which the plugin cannot tell apart
  from a user re-monitor whose row was lost, so it protects the single (see
  [Re-monitored singles](#re-monitored-singles)): it is not unmonitored again, and files that
  download for it are kept.

The eager unmonitor is therefore best effort: a redundant single can still be grabbed after it
ran.

What the plugin promises is conditional, and it is about the end state rather than the
ordering. A redundant single that ends up with downloaded files is cleaned (its files deleted)
only when **all** of these hold:

- The path that cleans it is switched on. The import-time half needs the Connect entry; the
  scan half needs the scheduled scan, which is **off by default**. With neither, the grab and
  download can still happen and the files stay.
- The files are the plugin's to clean by its History. Either the single was left monitored
  because a download was in the download client's queue or in Lidarr's tracked downloads
  (below), so the import-time cleanup sees a normal monitored single, or the single is still
  unmonitored by the plugin and every file came from an automatic grab (whenever Lidarr made
  it, including the post-refresh search above) or from a user grab that predates the
  unmonitor (the leak backstop, below).
- The normal cleanup rules still call it redundant (Tier-3 action, exact-audio, #17
  protection), and the delete itself succeeds.

Files stay, by design or by failure, when:

- the user fetched the release after the unmonitor through interactive search or a Search the
  user started (`ReleaseSource=InteractiveSearch` or `UserInvokedSearch`), or imported files
  that belong to no download. That is the user's own content, not a leak, and the backstop
  keeps it. A bulk Search the user starts counts too, which leaves a narrow gap (below);
- the grab provenance cannot be established (History lookup failure, missing import or grab
  rows, singles the plugin unmonitored before the History audit existed);
- the user re-monitored the single (its latest plugin row is `UserRemonitor`), or it is
  monitored again, by the user or by Lidarr's artist-add monitoring, while its latest plugin
  row is still `Unmonitor`. Either protects it from unmonitors and deletes while it holds;
- a delete fails.

Files are cleaned against the user's intent in one known case: if the user's own grab fails,
Lidarr re-searches automatically (`ReleaseSource=Search`) and the re-download is cleaned.

Wasted bandwidth is reduced, not eliminated: a download that was already in flight, or that
one of the paths above starts anyway, still downloads.

## Downloads in flight and the leak backstop

A single that has a download in the download client's queue or in Lidarr's tracked downloads
(grabbed, downloading, or awaiting import) has no files yet, so it looks like any other
file-less redundant single. Unmonitoring it would let the download
import into an unmonitored release. Every file-less unmonitor (the eager service, and the
Tier-1/Tier-2 no-files path in the scan and import cleanup) therefore first asks
`LidarrInFlightReleaseProbe`, which reads two things without issuing commands:

- Lidarr's tracked-download cache (the downloads Lidarr is following in the client; delay-profile
  pending releases, which Activity → Queue also lists, are not in it). Any state except Imported,
  Ignored, DownloadFailed and DownloadFailedPending counts as in flight.
- Grabbed History rows newer than 6 hours that are not tracked yet and have no
  DownloadImported/DownloadFailed/DownloadIgnored row. This covers the few seconds between a
  grab and Lidarr's debounced queue refresh, and the empty cache after a restart.

An in-flight single stays monitored (`reason=download-in-flight`), and the import-time
self-check cleans its files once they land. Delay-profile pending releases are not counted:
RSS sync re-checks them against the monitored flag, so unmonitoring cancels them.

Some downloads still get past the unmonitor: a grab dispatched just before it, an RSS sync
that decided on the monitored state it read and sent the release to the client just after, or
an automatic search Lidarr runs later. Lidarr searches unmonitored albums in several automatic
paths: the post-refresh `AlbumSearchCommand` (album ids are captured while still monitored and
the command runs after the rescan, often minutes later), Missing sweeps (the list is captured
when the sweep starts), `RedownloadFailedDownloadService` re-searches, and import-list sync.
`MonitoredAlbumSpecification` does not check the monitored flag for searches, so those grabs
are accepted. The cleanup paths therefore no longer skip every unmonitored release.

The **leak backstop** evaluates a release under the normal cleanup rules (Tier-3 action,
exact-audio, #17 protection) and deletes its files when they are redundant
(`reason=plugin-unmonitored-has-files`) only when all of the following hold:

1. the release is unmonitored and its latest plugin History row is the plugin's own
   `Unmonitor`;
2. every file it now holds traces, through Lidarr's `TrackFileImported` row (`FileId`,
   `DownloadId`), to a `Grabbed` row for the same download; and
3. each of those grabs is one of:
   - **automatic**: its `ReleaseSource` is anything other than `InteractiveSearch` or
     `UserInvokedSearch` (`Rss`, `Search`, `ReleasePush`, `Unknown`, missing, or a value the
     plugin does not recognise). These are cleaned whenever they were grabbed, before or after
     the unmonitor;
   - **user-initiated and earlier than the unmonitor**: its `ReleaseSource` is
     `InteractiveSearch` or `UserInvokedSearch` and it was recorded no later than 5 minutes
     after the `Unmonitor` row (a search sends approved releases to the client one at a time,
     so its `Grabbed` row can trail the decision it made while the single was monitored).

`ReleaseSource` comes from Lidarr's `ReleaseSourceType`, set in `DownloadDecisionMaker` from
the search criteria and stored on the `Grabbed` row; the plugin matches it case-insensitively.

Anything else keeps the files (`reason=leak-backstop-not-race`). Concretely, the backstop
keeps:

- grabs from interactive search, or from a Search the user clicked, recorded more than 5
  minutes after the unmonitor: that is the user fetching a single the plugin dropped;
- manual imports of files that belong to no download (their `TrackFileImported` row has no
  `DownloadId`), and files from a disk scan (Lidarr writes no import row for those);
- files whose import or grab row is missing, and any History lookup failure.

Known trade-off: if a user's interactive grab fails, Lidarr's failed-download handling
re-searches automatically (`ReleaseSource=Search`), and that re-download is cleaned. To keep
it, re-monitor the single or grab it interactively again.

Known gap: bulk searches the user starts (Wanted → Missing → Search All, or searching a
selection) record `UserInvokedSearch` for every album on a list captured while the single was
still monitored. If the plugin unmonitors the single mid-sweep and the grab lands more than 5
minutes later, it is kept as a user fetch. The in-flight probe covers most of these: a single
already grabbed when the plugin evaluates it stays monitored instead.

Limitation: Lidarr's History does not record whether an import was manual. A manual import
of a *tracked* download (Activity → Queue → Manual Import), or a folder import that Lidarr
matches to an outstanding grab, carries that download's `DownloadId`, so its grab's source
decides it like an automatic import.

The backstop re-reads its inputs immediately before deleting: the plugin's provenance row, the
stored monitored flag, and the files' grab provenance. Matching and exact-audio analysis can
take minutes, and a user re-monitor or fetch in that time aborts the delete
(`reason=backstop-state-changed`).

A release with no plugin row was unmonitored by the user or by Lidarr and is not touched by
the backstop. Plugin History rows only exist since the History audit was added (#47); singles
the plugin unmonitored before that have no row, so files that leaked into them are not cleaned
by the backstop.

### Re-monitored singles

If the user re-monitors a single the plugin unmonitored (the album monitoring toggle or
the album editor, which publish `AlbumEditedEvent`), the eager service records a
`UserRemonitor` History row, even when the single's artist cannot be resolved (the row is
keyed by album). While that row is the latest plugin row, neither the eager service nor the
cleanup paths unmonitor the release or delete its files.

A single that is monitored while its latest plugin row is still `Unmonitor` is protected the
same way. The plugin never re-monitors a release, so this state means it was re-monitored
without a `UserRemonitor` row being kept: the row write failed, the artist lookup threw, or
Lidarr's `CleanupOrphanedHistoryItems` housekeeping removed a row keyed to no artist. Lidarr's
own bulk monitoring writes (`AlbumMonitoredService` on artist add) publish no edit event, so
the plugin cannot tell them apart from this case; they protect the single too. This protection
lasts only while the single stays monitored: if it is unmonitored again by hand, the standing
`Unmonitor` row makes it plugin-unmonitored once more and the leak backstop applies.

A `UserRemonitor` row lasts until another plugin row supersedes it. Unmonitoring the single
again by hand writes no plugin row, so the plugin keeps leaving the single alone. There is no
setting or UI action to hand it back to the plugin; the only way is to delete the plugin's
History rows for that album from Lidarr's database.

Plugin rows are found by their `Plugin`/`Action` Data keys. Lidarr stores History Data as JSON
with a camelCase key policy, so rows read back from the database carry `plugin`/`action`; the
plugin matches these keys case-insensitively.

## Why not a grab-time hook (`NotificationBase.OnGrab`)

Lidarr notifications fire after `DownloadService` has already sent the download to the
client; a notification cannot veto a grab, so `OnGrab` cannot *prevent* a download.
Unmonitoring there would also race the download into an unmonitored release, which only the
leak backstop above would then clean. The lifecycle hooks cannot promise to run before every
grab either (see the caveats above), but they run early enough that most redundant singles
are not grabbed, and RSS sync, which reads the monitored flag from the database at
decision time, respects an unmonitor that has already been written.

## Settings

`Eagerly unmonitor redundant singles` follows the existing settings pattern and precedence
(Connect entry first, then the Metadata entry, then defaults). Existing installs pick up
the default (on) because Lidarr's settings reader keeps CLR defaults for fields absent from
the persisted definition. Turning it off restores the pre-#26 behavior completely: no
eager unmonitors, and the import/scan no-file path goes back to exact MBID (Tier-1) matches only.
