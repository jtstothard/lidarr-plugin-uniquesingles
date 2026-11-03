# Exact FLAC analysis and operations

The #39–41 implementation notes below record their original verification boundaries.
[Issue #42 acceptance](acceptance/issue-42.md) supersedes their outstanding macOS
disposable-host gate. The subsequent [Linux VM102 qualification](acceptance/issue-37-linux.md)
passed all 14 native stages with Ubuntu FFmpeg 6.1.1 and a separately identified
DLL. The [issue #49 qualification](acceptance/issue-49.md) adds native album-import
and fault-injection stages to the same disposable runner on macOS arm64. Historical
Linux-unverified statements below predate that bounded run. Windows remains
unqualified; see the Windows gap note below for exactly what qualification requires.

## Operating the feature

1. Supply a pinned local FFmpeg executable in the Lidarr runtime. The qualified
   build is FFmpeg 9.0.2, Homebrew 9.0.2_1 on macOS arm64. Use its absolute path
   in **Local FFmpeg executable**; verify permissions under the Lidarr account.
   No executable, shared library or license bundle is included in the plugin ZIP.
2. Enable **Exact FLAC analysis (report only)** in Connect, or Metadata if no
   Connect definition exists. Both audio toggles default off. Leave **Exact FLAC
   cleanup** off while reviewing evidence. Existing metadata cleanup still runs.
3. Manually run **UniqueSingles Scan** through Tasks/command dispatch, or import
   synthetic files first. Automatic scheduling requires enabling the Metadata
   provider; the minimum interval is 60 minutes and default is 1440. Disabling
   that provider removes its task. A completed schedule retains its execution
   time across restart rather than becoming overdue.
4. Read whole-release outcomes: `keep-exclusive` means retain because content is
   unmatched; `unresolved-review` means investigation remains;
   `report-only-exact-duplicate` means every catalog track has verified coverage.
   Track diagnostics remain available even when an exclusive B-side suppresses
   the release review count. `Unavailable` is never deletion proof. Scan/import
   summaries report exclusive-content keeps as their own `keepExclusive` aggregate
   (not inside `skipped`), and `cleaned` counts releases unmonitored — deletion is
   attempted but not guaranteed (watch `deleteFailures`).
5. **Exact FLAC cleanup** is a separate explicit opt-in and enables analysis itself.
   It uses the same import/scan path and requires every track plus immediate
   source eligibility and content revalidation. An abort or decoder failure
   preserves remaining files; after a deletion-stage failure a release can remain
   unmonitored, so inspect both monitoring and file outcomes.
6. Auditability outside plugin logs: unmonitor actions write a Lidarr History row
   per release (event type Unknown, `Plugin=UniqueSingles`, `Action=Unmonitor`,
   `Reason=…`), and plugin file deletions appear as Lidarr's own
   `TrackFileDeleted` history events because cleanup deletes through Lidarr's
   deletion service. History write failures are logged and never alter a cleanup
   decision.

To reproduce the disposable qualification, build Release, prepare the pinned host
archive, then run `python3 scripts/qualify-disposable-host.py` (Python 3.12+). It
creates a new random `_temp/issue49-*` directory, fresh database and synthetic
FLAC library, binds only loopback on an available port, and terminates its host
in `finally`. It never accepts an existing data/library directory. On macOS it
ad-hoc signs only its extracted disposable apphost. Retain `results.json` and
`host-*.log` as local receipts. No production configuration is an input.

## Operator contract

`ExactAudioAnalysis` defaults to false, including deserialization of older settings.
Without the separate `ExactAudioCleanup` opt-in, enabling it only reports otherwise unresolved downloaded singles. It adds no
unmonitor/delete authority, even with a fully verified release. Existing metadata
cleanup and the no-file Tier-1 path retain their existing behavior. In particular,
this setting is not a global dry run of metadata cleanup. Connect matching settings
win over persisted Metadata settings, then defaults; unbound command handlers read
persisted definitions. Import uses the bound Connect definition.

Eligible sources remain monitored downloaded tracks from the configured release
types, scoped to the same artist, excluding the candidate itself and Live, Remix,
DJ-mix and Demo releases. Audio work is limited to normalized title or recording-ID
candidates; duration tolerance is unchanged. Every catalog track on the candidate,
including missing tracks and alternate versions, needs its own verified counterpart
to report the whole release. Each release logs a final report-only or unresolved
decision; track evidence names both releases/tracks and recording IDs, catalog and
actual durations, policy, file-content identities and verification status. Only
complete releases increment `CleanupResult.ExactAudioReports`.

## Decoder selection and actual local assessment (2026-10-28)

The implementation uses an **optional operator-provided FFmpeg executable**. This
host is macOS 26.3 arm64, .NET SDK 10.0.103/runtime 10.0.3. It has Homebrew FFmpeg
**9.0.2**, formula revision **9.0.2_1**, at `/opt/homebrew/bin/ffmpeg`; neither
`flac` nor `fpcalc` is on PATH, and no standalone Homebrew libFLAC was found. The
observed FFmpeg build enables GPL and version3. No software was installed.

The qualification target is pinned here to FFmpeg 9.0.2 (Homebrew 9.0.2_1 on this
host). No NuGet/native decoder dependency or decoder binary is added or redistributed
by the plugin, and its archive remains DLL/PDB only. The adapter is capability
based rather than trusting a version banner: unsupported options/diagnostics or
missing frame information fail closed. Other versions/builds are not qualified by
this host run. Production deployment must choose and pin a suitable FFmpeg package
in its own image/host and run the synthetic acceptance suite there first; the plugin
does not download or install it. `ExactAudioDecoderPath` is an executable path inside
the Lidarr runtime (defaults to `ffmpeg` on that runtime's PATH), not a shell command.
A decoder on the developer Mac says nothing about the production container.

FFmpeg is available for Linux, macOS and Windows, but executable packaging,
architecture, libc/runtime and permissions are deployment concerns. The exact-audio
path is qualified natively on macOS arm64 ([#42](acceptance/issue-42.md),
[#49](acceptance/issue-49.md)) and Linux x86_64
([#37](acceptance/issue-37-linux.md)); the production container qualification is
recorded in [#43](acceptance/issue-43-production.md). A managed decoder would avoid
an executable deployment step but adds a new shipped dependency and needs
independent corruption/format qualification; libFLAC interop adds native
ABI/platform packaging. FFmpeg was selected because a usable local installation
exists and its native FLAC decoder and diagnostic seam can be exercised without
changing Lidarr core. No cloud service is involved.

**Windows qualification gap (open).** Windows has never executed this path and
remains unqualified — no claim is made. Qualifying it requires, at minimum: a
Windows host running the documented disposable suite (`scripts/qualify-disposable-host.py`)
end to end against a pinned Windows FFmpeg build, with the runner extended first
because it currently maps only `osx`/`linux` RIDs and its fault-injection decoder
wrappers are POSIX shell scripts that need Windows equivalents; retained receipts
naming the decoder executable SHA-256, environment and every stage outcome under
`docs/acceptance/`; and updates to the platform statements in this document to name
exactly the qualified platforms. The C# suite's POSIX-only delete-guard proofs and
process-lifecycle tests skip on Windows by design (Windows blocks the simulated
external in-place writer through mandatory share enforcement — the protection the
guard provides there — so the detection path cannot be exercised as on POSIX); a
Windows qualification must state that boundary rather than imply parity.

FFmpeg's normal license is LGPL 2.1 or later; enabling optional GPL components or
version3 changes the applicable license. See the upstream
[license](https://github.com/FFmpeg/FFmpeg/blob/master/LICENSE.md). Anyone shipping a
decoder/container must audit that exact build and provide its applicable notices,
license and corresponding source obligations. This plugin ships no FFmpeg code or
binary and invokes a separate local process. This is not a redistribution clearance
for arbitrary third-party builds. The plugin's own MIT license is unchanged.

## Verification and bounds

Policy `flac-original-packed-le-sha256-v1` accepts native FLAC containing signed
16-bit or 24-bit integer samples, 1–8 channels, a known positive sample rate and
sample count. Other precision/count-unknown streams are explicitly unavailable.
It reads an immutable bounded compressed snapshot and records its SHA-256. It then
fully decodes through the native FLAC decoder with CRC checks, explode and xerror,
without resampling, channel conversion, normalization, trimming or precision loss.
Automatic conversion filters are disabled. Every pre-encoder `ashowinfo` frame must
agree with STREAMINFO's format, channel count and rate; layout must remain constant.
The actual layout is compared across files. This guards against FFmpeg accepting
frames whose rate disagrees with STREAMINFO. A packed little-endian PCM encoder
preserves all original 16/24-bit samples. The full PCM SHA-256 and sample format,
layout, rate, precision and independently counted total samples must agree.
A nonzero stored PCM MD5 is independently checked against full decoded PCM; matching
stored checksums alone never establish equality. Missing/zero MD5 is permitted only
with the same full decoding, frame-integrity and sample-count checks.

One shared options snapshot permits 64 candidate pairs across the command/import,
including multiple artists; cache hits consume the same pair budget as misses. The
total is split into a 32-pair shared lane and a 32-pair lane reserved for
review-needed candidates: non-review candidates can only draw from the shared lane,
while review-priority candidates may draw from the whole budget. Scan and import
additionally order review-needed candidates (title-only matches that would flag for
review on metadata alone) before other candidates, so earlier candidates cannot
starve the review queue of comparison attempts — the 2026-10-29 production scan
previously left all 20 review-needed releases `comparison-budget-exhausted` against
a 32-pair budget consumed by earlier candidates. The `exact-audio release:` line
carries `reviewPriority=` to audit which lane applied. One process-wide
slot bounds decoder concurrency. Each pair has a 30-second deadline including slot
wait, cancellation, reading and both decodes. Each compressed input is at most
64 MiB; each decoded file at most 256 MiB; PCM is hashed incrementally and diagnostics
are drained with a bounded line buffer. These deliberately conservative limits
can defer long/high-resolution files. Budget/deadline/cancellation failures produce
unavailable evidence and cannot produce a complete duplicate report. Options expose
an operation cancellation token, and the scan command and release-import entry
points now link Lidarr's dispatcher lifecycle into it: Lidarr's synchronous plugin
entry points (`IExecute.Execute`, `NotificationBase.OnReleaseImport`) receive no
per-command token, so the hub token is cancelled from the `ApplicationShutdownRequested`
lifecycle event that the dispatcher itself also handles. On that signal the scan
stops before the next artist, in-flight decoder work returns unavailable evidence,
and exact-cleanup revalidation aborts to keep. Decoder children are killed and pipe
tasks joined on failure/cancellation. No performance benchmark is claimed.

## Subsequent tickets

`IExactAudioVerifier.Compare` is the narrow I/O seam. Its immutable `AudioEvidence`
contains status/reason, original sample descriptors, full PCM digest and content
SHA-256 for each compressed snapshot. `ExactAudioReleaseReport` carries candidate
and counterpart release/track IDs and paths with the verification evidence; the
reporter consumes the verifier interface. These records describe observed snapshots,
not reusable deletion permits. #40 can wrap this seam with a content/policy/decoder
identity cache, preserving unavailable evidence and invalidation. Paths, sizes and
mtimes alone must never be cache authority. Decoder identity must include the actual
binary/build, not only this code's policy constant.

#41 must separately introduce explicit cleanup opt-in, revalidate candidate/source
content identities immediately before any destructive action, check continued source
eligibility, maintain a survivor and preserve unmonitor/delete failure handling.
A source can disappear/change after this report; #39 deliberately makes no destructive
claim from it. No production library, deployment, live dispatcher or cleanup was
used for this ticket. Disposable native dispatcher acceptance remains an integration
step under #37.

## Reproduction

The checked-in fixtures contain deterministic generated integer samples, no music.
`tests/UniqueSingles.Test/AudioFixtures/generate.py` documents their construction and
can regenerate them with the qualified FFmpeg; tests need only the decoder, not the
encoder. Tests intentionally fail if the configured local decoder is absent rather
than silently skipping the feature's acceptance proof.

Run the documented Release build/test and Python archive-preparation gates in
`CONTRIBUTING.md`. The behavior tests exercise scan, single import and album import
with the real decoder/files and recording repositories/media services. They cover
tag/compression differences, wrong catalog IDs/durations, full multi-track coverage,
exclusive/missing/alternate tracks, source eligibility/self-exclusion, late samples,
forged/zero MD5, false header rates, corruption, truncation, formats, absence, budgets
and cancellation. Lower tests check 24-bit fidelity and decoder deadlines.

## Explicit exact cleanup (#41)

`ExactAudioCleanup` defaults false, including older saved settings. It independently
opts into analysis plus whole-release cleanup; analysis alone remains report-only.
Every catalog track must have a downloaded, fully verified retained counterpart.
The existing metadata and no-file Tier-1 paths are unchanged.

Before unmonitoring and again immediately before each file deletion, cleanup reloads
source eligibility and track/file mappings and compares current compressed-file
SHA-256 content against the verified snapshots. Extra unverified candidate files,
changed coverage, missing/replaced files, ineligible sources and lookup/hash failures
abort exact approval. Sources removed earlier in the operation cannot justify later
cleanup. An unmonitor failure prevents deletion; a later validation/deletion failure
leaves the release unmonitored and preserves remaining files. Logs record verification,
revalidation action, abort reason and normal unmonitor/delete outcomes.

**Delete-time guard (#49).** Each exact-audio file deletion anchors content identity
on an open handle instead of a path read: the file is opened read-only with write
sharing denied *before* the final per-file revalidation, the verified digest is
re-checked through that same handle, the handle stays open across Lidarr's deletion
call, and the pinned bytes are re-read afterwards. In-place mutation of the deleted
file between the final check and deletion is therefore blocked or detected: .NET's
share enforcement blocks same-process writers on every platform, Windows additionally
blocks external in-place writers by mandatory share enforcement while the handle is
held, and on POSIX — where sharing is advisory — the post-delete re-read through the
pinned handle detects any external in-place change, logs
`reason=mutation-detected-post-delete`, and aborts the remaining deletions. If the
guard handle cannot be opened at all (the file disappeared or was replaced),
cleanup fails closed (`reason=delete-guard-open-failed`) before any destructive call.
A post-delete re-read that cannot be performed also fails closed
(`reason=post-delete-verify-failed`). Non-exact cleanup paths (metadata matching,
Tier-1 no-file unmonitor) are unchanged and take no guard.

**Residual window (inherent, documented).** Deletion goes through Lidarr's
path-based deletion service so that Lidarr's own `TrackFileDeleted` history and
recycle-bin integration remain in force; the plugin cannot delete by handle without
bypassing them. Consequently an external rename-over of the path — replacing the
directory entry with different content between the guard's open and Lidarr's unlink —
remains an OS-level TOCTOU that the guard cannot close (the guard pins and verifies
the inode it verified, while the unlink acts on the path). Filesystem and repository
calls are still not one atomic transaction. The #49 fault-injection tests prove the
fail-closed outcomes for in-place candidate mutation, source mutation and sibling
removal inside the destructive window; rename-over replacement is not claimable as
closed and is accepted as the residual race, as with existing media deletion.

This contract consumes immutable verifier evidence and works uncached; a #40 wrapper
must supply the same content evidence and cannot bypass these checks. No production
settings were activated. Disposable native dispatcher acceptance under #37 remains outstanding.

## Content evidence cache (#40)

The verifier keeps at most 1024 successfully decoded file descriptors process-wide,
with FIFO eviction. It stores no audio buffers and persists nothing across restarts.
Keys include the full compressed-content SHA-256, policy constant, resolved executable
path and SHA-256 of the actual executable bytes (not a version label). Every lookup
reads the complete bounded current file; paths, lengths and timestamps grant no trust.
Different audio can reuse successful per-file decodes; failed/unavailable decodes are
never cached. Changing the policy or executable bytes forces fresh decoding.

Both paths and executable are hashed again before evidence returns, for fresh and
cached comparisons alike. Disappearance, replacement, cancellation or identity mismatch
returns unavailable. Concurrent changes after this final observation remain possible:
#41 must revalidate both evidence FileDigest values immediately before cleanup and
recheck source eligibility/survivor protections. Compare does not issue deletion permits.
The immutable AudioEvidence/AudioTrackEvidence contract is unchanged.

One process-wide slot serializes identity reads, cache access and decoding. The
command/import budget now totals 64 pairs (32 shared plus a 32-pair review-reserved
lane) and still applies to hits; each comparison still has the shared
30-second deadline. Inputs remain 64 MiB each, decoder identity reads at most 256 MiB,
and decoded PCM remains capped at 256 MiB. Cancellation is checked between identity
read chunks; an OS filesystem read itself can block beyond the deadline. No separate
scheduler, external cache or performance guarantee is introduced. A verifier constructed
with useCache:false follows the same identity and safety checks without evidence reuse.

Use a directly configured, pinned decoder executable. Executable hashing identifies
that file/build; it does not attest transitive shared libraries, interpreter scripts or
runtime-loaded plugins. Deployment changes to those dependencies require restarting
Lidarr to discard process-local evidence and rerunning decoder qualification. Linux,
Windows, production deployment and live dispatcher acceptance remain unverified.

## Follow-up observability, cancellation and budgeting (#44)

The `exact-audio release:` line reports the authority state the run carries from
settings — `cleanupAuthority=exact-audio-cleanup` when the explicit cleanup opt-in
is enabled, `cleanupAuthority=none` for report-only analysis — instead of a
hard-coded literal. The field describes configured authority, not a grant: every
actual destructive approval still appears only on the later
`exact-audio cleanup: … decision=revalidated` lines. The same line carries
`reviewPriority=` recording which budget lane the release used.

Decode failures keep their `decode-unavailable-or-invalid:` prefix and now append a
stable token naming the adapter's own integrity gate that failed (for example
`invalid-streaminfo`, `frame-disagrees-with-streaminfo`,
`streaminfo-digest-disagreement`, `unsupported-format`). Framework exceptions keep
only the exception type name; executable stderr, paths and framework exception
messages are never structured evidence. The 2026-10-29 production decode failure
against Against Me! "Pretty Girls (The Mover)" was `decode-unavailable-or-invalid`
with no gate detail; future occurrences will name the gate without code changes.
The production file itself was out of scope for this ticket.

Summary counters reconcile with per-release `outcome=` lines: `keepExclusive`
counts `outcome=keep-exclusive` releases separately from `skipped` (which still
includes unresolved reviews alongside their `reviewNeeded` count), and `cleaned`
counts releases unmonitored — file deletion is attempted after unmonitoring but
is not guaranteed, so audit `deleteFailures` alongside it.

Cleanup actions are auditable outside plugin logs. Unmonitor actions write a
Lidarr History row per release through the host's own history repository, with
event type Unknown (Lidarr has no unmonitor event), `SourceTitle=UniqueSingles`
and `Plugin`/`Action`/`Reason` data; file deletions already appear as Lidarr's
`TrackFileDeleted` history events because the plugin deletes through
`IDeleteMediaFiles`, whose `MediaFileDeletionService` → `MediaFileService`
publishes `TrackFileDeletedEvent` and Lidarr's `EntityHistoryService` records it.
History write failures log a warning and never alter a cleanup decision.
