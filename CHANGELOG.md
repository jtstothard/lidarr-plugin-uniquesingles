# Changelog

All notable changes to Unique Singles will be documented in this file.

## [Unreleased]

### Fixed

- Candidate-side secondary-type protection, and Tier-1-only compilation sources. A single that itself carries a Live, Remix, DJ-mix or Demo secondary type was still selected as a cleanup candidate, so a "Single + Live" or demo release whose track had the bare studio title and a length within tolerance was Tier-2 matched against studio albums and had its files deleted — the mirror image of the source-side exclusion from #31. Such a single is now never unmonitored or deleted on any tier, Tier-1 included, in the scan, album import, self-check, review ordering, exact-audio cleanup and eager paths (`reason=candidate-excluded-secondary-type`). Source releases carrying any other secondary type (Compilation, Soundtrack, Spokenword, Interview, Audiobook, Mixtape/Street, Audio drama) now contribute exact recording-MBID (Tier-1) evidence only, since a re-recorded hit or radio edit on a "Greatest Hits" shares the bare title and a near-equal length with the studio single (the #31 description of compilations matching "reliably via MBID" applied to Tier 1 only). A null `SecondaryTypes` is now treated as unknown rather than none: such a release is never a comparison source and such a single is never a candidate. Residual: an empty but stale list (metadata not yet refreshed) cannot be told from a genuine "no secondary types".
- Exact-audio deletion now anchors the retained source, and a symlinked source is no longer mistaken for a second copy. After each deletion the plugin re-verifies that the retained source still exists with its verified content, and a source that vanished or changed inside the destructive window (`reason=source-vanished-post-delete`) counts as a delete failure and aborts the remaining deletions instead of silently leaving zero verified copies. The `source-shares-candidate-path` check now resolves symlinks in the file and its parent directories, so a "source" symlinked to the candidate is rejected; hardlinked sources remain valid. A deletion that has already happened cannot be undone, so this is detection and fail-closed, not prevention. See `docs/exact-flac-analysis.md`.
- Redundant singles that were already grabbed or downloading no longer leak their files. A file-less single was unmonitored even when its download was in flight; the download then imported into an unmonitored release, and the import cleanup skipped it as already unmonitored, so the files stayed forever, neither wanted nor cleaned. PR #59 widened this to Tier-2 matches. Now (1) every file-less unmonitor (eager service, scan, and import, Tier-1 and Tier-2) first checks Lidarr's tracked downloads and recent grab history, and keeps a single with a download in flight monitored so the import self-check cleans it; (2) files that still land in a release the plugin unmonitored, identified by its own History row, are evaluated under the normal cleanup rules instead of skipped, while releases the user or Lidarr unmonitored are never touched; (3) when the user re-monitors a single the plugin unmonitored, a `UserRemonitor` History row is recorded and the plugin never unmonitors that release or deletes its files again. See `docs/eager-unmonitor.md`.
- Deleting a redundant single now removes its leftover folder (#65). Only the audio files were deleted before, so sidecars such as `album.nfo`, `folder.jpg` and `.lrc` lyrics kept the folder alive and Lidarr's empty-folder cleanup skipped it. After every audio file of a single is deleted, a new `SingleFolderCleanup` sends the folder's recognized sidecars (album artwork names, `.nfo`, `.lrc`, `.m3u`/`.m3u8`, `.DS_Store`/`Thumbs.db`/`desktop.ini`) to Lidarr's recycle bin under the same subfolder Lidarr uses for the audio, then removes the folder with a non-recursive, remove-if-empty delete, so anything that lands in the folder meanwhile is never deleted. Any other content (untracked audio, videos, booklets, cue sheets, rip logs, hidden files, subfolders, another release's tracked files) keeps the folder untouched and is logged. Multi-disc layouts are cleaned innermost first, then the release folder. The scan also sweeps folders stranded by earlier versions, but only where Lidarr History attributes them to a plugin cleanup (a plugin Unmonitor row plus Lidarr's TrackFileDeleted row within an hour, single still unmonitored); singles cleaned before the History rows existed (#44) cannot be attributed and are left alone. Based on #66 by Jason Krause. See `docs/single-folder-cleanup.md`.
- File-less singles that were already wanted before their album was downloaded now get unmonitored when their songs are on it. The scheduled scan and album-import cleanup only unmonitored a single with no files on exact MBID (Tier-1) matches, and the eager Tier-1/Tier-2 rule (#26) only ran when a single was added or its monitoring changed, so a single whose recordings carry different MBIDs from the album's (another mastering or edit within the duration tolerance) stayed wanted forever. In production 25 of 565 missing singles were in this state. Scan and import now apply the eager rule too when **Eagerly unmonitor redundant singles** is on: every catalog track must have a Tier-1/Tier-2 match against downloaded tracks on monitored comparison releases. Unchanged: never deletes (there are no files), #17 whole-release protection (any unmatched track keeps the single), title-only (Tier 3) matches never unmonitor, an undownloaded album is not a comparison source, and the History audit row is written as for other unmonitors.

### Added

- Eager pre-grab unmonitoring for redundant singles (#26). A new `EagerSingleUnmonitorService` subscribes to the album lifecycle events where a single becomes grabbable — `AlbumInfoRefreshedEvent` (metadata refresh / profile switches add singles as monitored), `AlbumAddedEvent` (manual add), `AlbumUpdatedEvent` (album refresh), and `AlbumEditedEvent` (monitoring toggles/edits) — and unmonitors a monitored single immediately when it has no downloaded files and every catalog track has a Tier-1/Tier-2 redundant match against the same comparison pool the cleanup path uses (downloaded tracks on monitored Album/EP releases, excluding Live/Remix/DJ-mix/Demo secondary types). Redundant singles are therefore never grabbed and downloaded in the first place, instead of being downloaded and deleted on import. Safety mirrors the cleanup path exactly: #17 whole-release protection (any unmatched catalog track, downloaded or not, keeps the single monitored), Tier-3 title-only evidence never unmonitors, releases with downloaded content are untouched, unmonitors are recorded in Lidarr History, lookups and failures fail safe to keep-monitored, and the service has no command-queue/search/download/delete dependencies so it can never trigger a grab. A grab-time notification hook was evaluated and rejected: notifications fire after the download is dispatched and unmonitoring there would leak files, since import-time cleanup skips already-unmonitored releases. New **Eagerly unmonitor redundant singles** setting (default on, matching the issue's intent; untick to opt out) following the existing settings precedence. See `docs/eager-unmonitor.md`.

### Changed

- Whole-release metadata protection now covers undownloaded catalog tracks (#17). The metadata cleanup path previously evaluated only a candidate release's downloaded tracks, so a redundant downloaded A-side could be unmonitored and deleted even though the release also held an exclusive undownloaded B-side with no counterpart anywhere in the comparison pool. Every catalog track now protects the release: any track without a Tier-1/Tier-2 redundant match — downloaded or not — yields keep-exclusive semantics and no cleanup of that release (no file deletion, no unmonitor), making the metadata path consistent with the strict all-catalog-tracks rule the exact-audio path already applies. Releases whose catalog tracks all match redundantly keep their existing behavior, as does the no-files Tier-1 MBID unmonitor path, which already required full-catalog redundancy; keep-exclusive precedence over review, Tier-3 actions, review-priority budgeting, the delete guard, and summary counters are unchanged.

- Main-branch CI builds now publish real GitHub releases instead of drafts, with `make_latest: true` so the newest `1.0.2.<run_number>` build is the release Lidarr's update check offers and installs. Draft releases were the root cause of #25 (users told an update exists but had nothing to install). Pull-request builds remain unpublished. (#25)

- CI now builds and runs the full test suite on `windows-latest` as Windows evidence toward #49: the pinned Lidarr reference set gains a SHA-256-verified `windows-core-x64` archive (zip extraction with the same top-level-DLL boundary), and the audio tests use Chocolatey's FFmpeg. POSIX-only process-lifecycle proofs keep their deliberate Windows guards.

### Added

- Delete-time verification guard for exact-audio file deletions (#49). Every exact-audio deletion now opens the candidate read-only with write sharing denied before the final per-file revalidation, re-checks the verified content digest through that same open handle, keeps the handle across Lidarr's deletion call, and re-reads the pinned bytes afterwards. An in-place mutation between the final check and the actual deletion is now blocked (in-process writers on all platforms; external writers on Windows by share enforcement) or detected and the remaining deletions aborted (`reason=mutation-detected-post-delete`); a file that disappeared or was replaced fails closed before any destructive call (`reason=delete-guard-open-failed`). The inherent rename-over TOCTOU that remains because deletion goes through Lidarr's path-based service is documented in `docs/exact-flac-analysis.md`. Metadata and Tier-1 cleanup paths are unchanged.

- Native album-import and fault-injection qualification stages (#49). The disposable-host runner now also drives the album-import notification route through the real dispatcher (parity with the proven single-import route) and injects faults into the native host: mid-analysis source mutation through the decoder seam, corrupt-input decode failure, decoder crash, unmonitor-failure injection via a disposable-database trigger, delete-failure injection via a read-only directory, and shutdown cancellation mid-decode. Each stage asserts release preservation or accounted `unmonitorFailures`/`deleteFailures` plus recovery. Receipt: `docs/acceptance/issue-49.md`. Windows remains unqualified.

- Lidarr History audit rows for plugin unmonitor actions (#44). Plugin file deletions were already recorded by Lidarr's own `TrackFileDeleted` history events because cleanup deletes through Lidarr's deletion service; unmonitors now additionally write album-level rows attributed to `UniqueSingles` (event type Unknown, `Plugin`/`Action`/`Reason` data), and a history write failure never affects the cleanup decision.

- Dispatcher cancellation wiring for exact-audio analysis (#44). The scan command and the release-import notification link Lidarr's `ApplicationShutdownRequested` lifecycle signal (the dispatcher signal available to synchronous plugin entry points) into the audio-analysis cancellation option; a shutting-down host stops the artist loop between artists and abandons in-flight decoder work as unavailable evidence.

- Review-priority comparison budgeting for exact-audio analysis (#44). The per-command budget is raised from 32 to 64 pair comparisons and split: 32 shared plus a 32-comparison lane reserved for review-needed candidates, and review-needed candidates are analyzed before other candidates in a scan/import. Earlier candidates can no longer starve the review queue of comparison attempts, resolving the 2026-10-29 production scan where all 20 review-needed releases ended `comparison-budget-exhausted`.

- Reproducible disposable Lidarr command/import qualification with synthetic FLAC files and operations evidence (#42).

- Separate default-off exact FLAC cleanup opt-in with complete-track coverage, immediate content/source revalidation, and scan/import adversarial regressions (#41).


- Exact FLAC analysis reuses bounded in-memory evidence keyed by content, verification policy and decoder executable SHA-256, revalidating both files before returning evidence.

- Opt-in exact FLAC analysis reports verified whole-release duplicates through scan/import without adding cleanup authority. Requires a local FFmpeg decoder; see `docs/exact-flac-analysis.md`.

- `CONTRIBUTING.md`, `SECURITY.md`, and `CODE_OF_CONDUCT.md`
- Issue and pull request templates
- Dependabot config for NuGet and GitHub Actions dependencies
- CodeQL security scanning workflow

### Fixed

- The `exact-audio release:` evidence line now reports the real cleanup authority state (`cleanupAuthority=exact-audio-cleanup` when the explicit cleanup opt-in is enabled, `none` for report-only) plus a `reviewPriority=` label, instead of a hard-coded `cleanupAuthority=none` even when cleanup proceeds (#44). Actual destructive grants remain on the later `decision=revalidated` lines.

- Scan and import summaries report exclusive-content keeps as their own `keepExclusive` aggregate instead of folding them into `skipped`, so counters reconcile with the per-release `outcome=` lines; completion messages now state that `cleaned` counts releases unmonitored (deletion not guaranteed) (#44).

- Decode failures surface a stable reason token naming the failed integrity gate (for example `decode-unavailable-or-invalid:streaminfo-digest-disagreement`) instead of only the exception type name, without logging executable stderr, paths or framework exception text (#44).

- Report-only exact-audio analysis preserves existing Tier-3 AutoClean authority on scan and import (#37).

- Unbound handlers now prefer persisted Metadata settings over their host-created default settings. Incomplete exact-audio analysis includes undownloaded exclusive tracks in keep/review outcomes.
- Restored plugin identity/package smoke compilation and Release DLL/PDB ZIP generation; CI publishes the tested archive.

- Scan/import outcomes now distinguish exclusive-content keeps from unresolved reviews. Exclusive tracks suppress release-level review flags; multiple title-only tracks count as one review, with track evidence retained.

- Resolve scan settings for unbound command handlers, preventing a crash when no Connect entry exists and preserving Metadata settings fallback.

- With `Single` in **Release types to compare**, every downloaded single matched its own tracks and was deleted. A single is now never compared against itself, and of two identical singles only one is removed.
- Release builds now carry the release version. Every build previously reported 1.0.2.0, so Lidarr always offered an update that never cleared after installing it (#25).
- The scheduled scan no longer runs on every Lidarr restart. Lidarr removes plugin task rows at startup; the plugin now restores the previous run time instead of scheduling the scan as overdue.

### Changed

- Documented that **Scan interval** is only read from the Metadata entry, that the scheduled scan uses the Connect entry's matching settings, and that singles without files are unmonitored on exact Tier 1 matches.
- Albums and EPs with a Live, Remix, DJ-mix, or Demo secondary type are no longer used as comparison sources. A live track with the same title and a similar length could previously cause the studio single to be deleted.

- The scheduled full-library scan only runs when **UniqueSingles Scan** is enabled under Settings → Metadata. Previously it was scheduled on every install, and disabling or removing the Metadata entry did not stop it.
- Build against checksum-pinned official Lidarr binaries instead of recompiling its source and dependency tree.

## [1.0.2] - 2025-05-28

### Added

- Import-triggered redundant single cleanup — detects when a single's tracks already exist on a monitored album or EP
- 3-tier matching strategy: MusicBrainz Recording MBID (Tier 1), title + duration (Tier 2), title-only (Tier 3)
- Scheduled full-library scans with configurable interval
- Configurable settings via Lidarr Connect UI:
  - Duration tolerance for title + duration matching
  - Release types to use as comparison sources
  - Title-only match action (flag for review, skip, or auto-clean)
  - Scan interval
- Unmonitors redundant singles and deletes their files
- Conservative behavior: ambiguous matches are flagged for review, never auto-deleted by default

### Known Issues

- Requires Lidarr nightly branch (plugin support not in stable)
- Tubifarry QueueCleaner coexistence may cause a plugin dependency conflict
- Scheduled scan appears under Settings → Metadata due to Lidarr's limited plugin scheduling API
