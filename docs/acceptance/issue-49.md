# Issue #49: delete-time mutation guard and native fault-stage qualification

Ticket: [#49](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/49).
Run date: 2026-11-03. Native run directory:
`_temp/issue49-b00d354472f84b8dbc5ee445c632096f` (23 stages, `completed=true`,
runner exit 0).

Parent specifications: [#41](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/41)
(explicit exact cleanup), [#42](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/42)
(disposable macOS qualification), [#37](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/37)
(Linux qualification). This receipt covers two of issue #49's three criteria — the
deletion mutation race and the native album-import/fault-stage proof. **The Windows
qualification criterion is not met and not claimed**; see the gap section below.

## Product change: delete-time verification guard

`SingleCleanupService` exact-audio deletions no longer verify the deleting file by a
fresh path read. For each file deletion under `ExactAudioCleanup`:

1. The file is opened read-only with write sharing denied (`FileShare.Read |
   FileShare.Delete`) **before** the final per-file revalidation.
2. `RevalidateExactCleanup` re-checks the verified content digest **through that same
   open handle** (all other invariants — source eligibility, whole-release coverage,
   survivor/self protections, extra-file exclusion — are unchanged and re-checked per
   file).
3. The handle stays open across Lidarr's `IDeleteMediaFiles.DeleteTrackFile` call.
4. The pinned bytes are re-read afterwards and compared with the verified digest.

Observed behavior by mutation class:

- In-process .NET writer: blocked outright by the handle's share mode on every
  platform (this fell out of the guard for free and is covered by the test suite's
  failure to open the simulated writer).
- External in-place writer between final check and deletion (POSIX): detected by the
  post-delete re-read → `reason=mutation-detected-post-delete`, remaining deletions
  aborted, release left unmonitored with remaining files preserved, `deleteFailures`
  accounted.
- External in-place writer on Windows: blocked while the handle is held (mandatory
  share enforcement). Not executed here — Windows is unqualified.
- Candidate removed/replaced before its own deletion: the guard cannot be opened →
  `reason=delete-guard-open-failed`, fail closed before any destructive call.
- Post-delete re-read impossible: `reason=post-delete-verify-failed`, fail closed.

Nothing else changed: metadata matching cleanup, Tier-1 no-file unmonitor, report-only
analysis, FlagOnly semantics, survivor/exclusive-keep protections and the evidence
cache contract are untouched, and non-exact deletions take no guard.

### Residual race (documented, not claimed closed)

Deletion goes through Lidarr's path-based deletion service (for its history and
recycle-bin integration), so an external rename-over of the path between guard open
and unlink remains an OS-level TOCTOU the guard cannot close. It is documented in
`docs/exact-flac-analysis.md` rather than claimed as fixed.

## Adversarial tests (cleanup-service seam, real synthetic FLAC)

Nine new test cases in `ExactAudioCleanupTests.cs` across the three routes (scan,
single-import self-check, album-import artist-cleanup), using a two-file verified
release on real temporary FLAC files and a `BeforeDelete` hook inside the destructive
call — the exact externally-mutable window:

- `ExactCleanup_CandidateMutatedInsideDeleteCallIsDetectedAndStopsRemainingDeletions`
  — an external process (`/bin/cp` of a mutated copy over the path, same inode)
  mutates the first candidate file between the final revalidation and the unlink:
  mutation detected post-delete, second file preserved, one deletion attempted,
  `DeleteFailures=1`, release unmonitored.
- `ExactCleanup_SourceMutatedInsideDeleteCallStopsRemainingDeletions` — the retained
  source is mutated in place during the first deletion; the next per-file
  revalidation re-hashes sources and aborts (`source-content-changed`) with the
  remaining file preserved.
- `ExactCleanup_CandidateRemovedInsideDeleteCallFailsClosedBeforeNextDeletion` — a
  sibling candidate disappears inside the window; the next guard open fails closed
  and no destructive call is made for unverifiable content.

The simulated external writer is a separate process because in-process writers are
blocked by the guard's share mode (which the first run demonstrated accidentally).
The three mutation cases are POSIX-proof tests and skip on Windows with an explicit
comment; the full suite passes on macOS arm64 here, and Linux CI (ubuntu-latest with
FFmpeg) runs the same suite on every PR.

## Native qualification (macOS arm64 disposable host)

- macOS 26.3.1 arm64; native Lidarr **3.1.3.4968** develop archive
  (`Lidarr.develop.3.1.3.4968.osx-core-arm64.tar.gz`, SHA-256
  `7fbebd4e5a5b01a8489a5dc733d7bb40c6881a42ccc87d32ace72640257bb725` — the same
  pinned archive as the #42 qualification), fresh database, loopback-only listener
  on an available port, ad-hoc-signed disposable apphost.
- Tested plugin DLL SHA-256:
  `8887185bed38f7226569a39a3a8b011414f688e1458a5ef54e42652ed550e3da`
  (Release build of this branch's working tree at qualification time; base commit
  `470e8ce4373cc2d743bfb539904a27d372711781` on `origin/main` plus this change).
- Decoder `/opt/homebrew/Cellar/ffmpeg/9.0.2_1/bin/ffmpeg`, FFmpeg **9.0.2**
  (Homebrew 9.0.2_1), executable SHA-256
  `768609fb3f463ff9272bebac61478515f19e43c5343b0c60dde2e6230c40013e` — identical
  to the #42-qualified decoder. Build tools: .NET SDK **10.0.103**; the test
  runtime used `RollForward` against net8.0 targets; native Lidarr used its
  bundled runtime.
- Fault-injection decoder wrappers (executable shell scripts inside the run
  directory) and the disposable-database trigger exist only inside the disposable
  run directory; no production or host-global state was touched.

The disposable runner (`scripts/qualify-disposable-host.py`) was extended with a
second synthetic artist and nine new native stages after the unchanged 14 stages of
the issue-42 suite. **All 23 stages passed in one uninterrupted run** (exit 0,
`results.json` `completed=true`). Final disposable-library state reconciles with the
stage outcomes: album-import-cleaned 600 and recovery-cleaned 604/606 have no files
and are unmonitored; fault-preserved 601/602/603 files remain on disk; delete-failed
605 remains on disk unmonitored (the documented steady state); the retained source
album 500 stays monitored with its imported file.

15. **`album-import-cleanup`** — a real `ManualImport` into the retained **Album**
    fires the Connect notification's album-import route through the actual dispatcher
    (`route=artist-cleanup`), exact-verifies the candidate single against the imported
    file, unmonitors and deletes it through the real deletion service **with the new
    delete-time guard active** (`decision=revalidated action=delete`); the imported
    album file and its monitoring state remain intact. This is the album-import parity
    the issue-42 receipt listed as seam-only.
16. **`mid-analysis-mutation-preserves-release`** — a decoder wrapper replaces the
    candidate file between the verifier's identity read and its decode; the adapter's
    own post-decode recheck rejects its evidence (`identity-changed-during-analysis`),
    the release stays monitored with its (mutated) file, one review flagged. (Source
    mutation *between scans* was already native in the issue-42 stage
    `changed-source-rejects-cached-evidence`; delete-time source mutation is proved at
    the seam above.)
17. **`decode-failure-mid-analysis-preserves-release`** — a corrupt candidate FLAC
    fails the real FFmpeg decode mid-analysis (`decode-unavailable-or-invalid`);
    release preserved, review flagged.
18. **`decoder-crash-preserves-release`** — the decoder process is killed by a signal
    (`kill -9`) during analysis; unavailable evidence, release preserved.
19. **`unmonitor-failure-injection-preserves-files`** — a trigger in the disposable
    database makes the album unmonitor write fail
    (`RAISE(ABORT, 'issue49 injected unmonitor failure')`); the command completes
    with `1 unmonitor failures` accounted, no deletion attempted, file preserved,
    album still monitored. The follow-up **`unmonitor-failure-recovery-cleans`**
    drops the trigger and cleans the release on the next scan.
20. **`delete-failure-injection-files-preserved`** — the candidate's directory is made
    read-only so the native deletion fails; the release is unmonitored
    (`1 delete failures` accounted) and its file remains on disk — the documented
    steady state after a deletion-stage failure.
21. **`shutdown-cancellation`** — a sleeping decoder wrapper marks a file when the
    decode starts; the runner SIGTERMs the host mid-decode (marker proves the decode
    was in flight). Lidarr's shutdown does not wait for the in-flight plugin command,
    so the cancellation evidence line is not guaranteed to be drained before process
    exit — observed tokens are recorded in the receipt as best-effort. The safety
    outcome is asserted deterministically: no revalidation approval, no unmonitor and
    no deletion line exists for the release being analyzed, the candidate file
    survives and the album stays monitored. The follow-up
    **`cancellation-recovery-cleans`** rescans with the real decoder and cleans the
    release normally. (The dispatcher-cancellation wiring itself — hub token,
    `ApplicationShutdownRequested`, in-flight decode returning unavailable — is proved
    deterministically by the C# suite's `ExactAudioFollowupsTests` and verifier
    cancellation tests.)

Unmonitor-failure injection, delete-failure injection, mid-analysis mutation, decode
failure, decoder crash and cancellation are all native here (none is satisfied only
at the cleanup-service seam); delete-time candidate/source mutation and sibling
removal are proved at the seam because deterministic mid-window mutation inside the
native host's own delete call cannot be timed from outside the process. Every host
process was terminated by the runner's `finally`; all modified/deleted files were
generated synthetic FLAC inside the run directory. Local receipts: `results.json`
(all 23 command responses, host/decoder/plugin/archive identities, scheduling
timestamps, cancellation-stage evidence) and `host-*.log` (plugin decisions, import
routes, guard revalidation, injected-failure warnings) remain in the run directory;
they contain no API key.

## Regression gates

- Full C# suite: **294 passed, 0 failed, 0 skipped** (285 pre-existing plus the 9 new
  adversarial cases) on .NET SDK 10.0.103 with `RollForward` against net8.0 targets.
  Test receipt: `_temp/test-results/issue49.trx`.
- `python3 -m unittest discover -s tests -p 'test_prepare_lidarr.py'` — 5 passed.
- Clean non-incremental Release build, 0 errors, pre-existing warnings only.
- `git diff --check` clean.

## Windows qualification: not executed, not claimed

Issue #49's Windows criterion remains **unchecked**. No Windows host was available in
this environment and nothing here qualifies Windows. What a real Windows
qualification requires is documented in `docs/exact-flac-analysis.md`: the disposable
runner needs a Windows RID mapping and Windows equivalents of its POSIX fault
wrappers, a pinned Windows FFmpeg build, retained decoder/environment/stage receipts
under `docs/acceptance/`, and updated platform statements. The POSIX-only delete-guard
tests skip on Windows by design; Windows blocks the simulated external in-place
writer through mandatory share enforcement, so a Windows run must state that boundary
explicitly.

No production host, settings, library or deployment was touched. No push, merge or
issue closure was performed by the qualification run.
