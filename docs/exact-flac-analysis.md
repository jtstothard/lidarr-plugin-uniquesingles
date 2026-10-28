# Exact FLAC analysis (#39)

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
architecture, libc/runtime and permissions are deployment concerns. This delivery
has only executed on macOS arm64. Linux/Windows and the production container remain
unverified. A managed decoder would avoid an executable deployment step but adds a
new shipped dependency and needs independent corruption/format qualification;
libFLAC interop adds native ABI/platform packaging. FFmpeg was selected because a
usable local installation exists and its native FLAC decoder and diagnostic seam
can be exercised without changing Lidarr core. No cloud service is involved.

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

One shared options snapshot permits 32 candidate pairs across the command/import,
including multiple artists; each pair decodes both files uncached. One process-wide
slot bounds decoder concurrency. Each pair has a 30-second deadline including slot
wait, cancellation, reading and both decodes. Each compressed input is at most
64 MiB; each decoded file at most 256 MiB; PCM is hashed incrementally and diagnostics
are drained with a bounded line buffer. These deliberately conservative limits
can defer long/high-resolution files. Budget/deadline/cancellation failures produce
unavailable evidence and cannot produce a complete duplicate report. Options expose
an operation cancellation token; the existing synchronous Lidarr entry points do
not yet supply a dispatcher cancellation token. Decoder children are killed and pipe
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

This contract consumes immutable verifier evidence and works uncached; a #40 wrapper
must supply the same content evidence and cannot bypass these checks. Filesystem and
repository calls are not one atomic transaction: external changes after the final
check remain a race, as with existing media deletion. No production settings were
activated. Disposable native dispatcher acceptance under #37 remains outstanding.
