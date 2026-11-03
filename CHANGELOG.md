# Changelog

All notable changes to Unique Singles will be documented in this file.

## [Unreleased]

### Changed

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
