# Issue #42: disposable exact-audio qualification

Parent specification: [#37](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/37).
Ticket: [#42](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/42).
Run date: 2026-10-28. Implementation tested in the native host:
`e967c0ffffcb1562047b0152057b128005bb5886`, descended from integration
`529de41d0f9ad69f68de89f5cd7e5586417db75e` (#38–41). Subsequent acceptance
receipt commits change documentation only. No production deployment/settings/library
were used. All deleted files were generated synthetic FLAC in this run directory.

## Host and reproducible commands

- macOS 26.3.1 arm64; native Lidarr **3.1.3.4968**, develop archive, bundled
  .NET **8.0.27**, SQLite **3.50.4**, fresh database migrated to version 80.
- Archive `Lidarr.develop.3.1.3.4968.osx-core-arm64.tar.gz`, SHA-256
  `7fbebd4e5a5b01a8489a5dc733d7bb40c6881a42ccc87d32ace72640257bb725`.
  The unsigned extracted macOS apphost initially exited 137 before logging;
  `codesign --force --sign - <disposable-host>/Lidarr` enabled startup. Only the
  extracted disposable executable was signed; no global security setting changed.
- Tested plugin DLL SHA-256:
  `2f8506e8b6ea3acfa6440a59bbc6e6a0115d7cf26eccb53ba763d2e8beca3f0f`.
- Local FFmpeg **9.0.2**, Homebrew **9.0.2_1**, executable
  `/opt/homebrew/Cellar/ffmpeg/9.0.2_1/bin/ffmpeg`, SHA-256
  `768609fb3f463ff9272bebac61478515f19e43c5343b0c60dde2e6230c40013e`.
  Its GPL/version3-enabled build was already installed. No decoder was bundled,
  installed or downloaded by the qualification runner.
- Build tools: .NET SDK **10.0.103**, test runtime **10.0.3** via existing test
  RollForward; native Lidarr itself used its bundled runtime, not that test runtime.

From the repository root:

```sh
python3 scripts/prepare-lidarr.py
dotnet restore UniqueSingles.sln
dotnet build UniqueSingles.sln -c Release --no-restore
dotnet test tests/UniqueSingles.Test/UniqueSingles.Test.csproj -c Release --no-build --logger 'trx;LogFileName=issue42.trx' --results-directory _temp/test-results
python3 -m unittest discover -s tests -p 'test_prepare_lidarr.py'
python3 scripts/qualify-disposable-host.py
```

The runner expands the verified full archive, installs only the built plugin under
`data/plugins/jtstothard/UniqueSingles`, starts the actual executable with
`-nobrowser -data=<new-run>/data`, and uses the HTTP API on a random loopback port.
It seeds stopped disposable SQLite with one synthetic artist, release/track/file
records and non-default persisted Metadata settings, then restarts. It updates the
host-created Metadata singleton rather than adding duplicate provider definitions.
Commands are real `POST /api/v1/command` requests (`UniqueSinglesScan`,
`ManualImport`); their terminal responses are retained. Provider changes use the
Metadata/Connect APIs. Every host started by the runner was terminated.

Full local receipts remain at
`_temp/issue42-67d9928a9a044f56a5f02d4c644e0f9f/` and
`_temp/test-results/issue42.trx`. The checked-in
[host receipt](issue-42-host.json) retains host identity, exact command responses,
artifact identities and scheduling timestamps; [host log excerpts](issue-42-host.log.txt)
retain plugin decisions, actual import-handler outcomes and file deletion evidence.
They contain no API key. Unrelated core tasks were omitted from the copied receipt.

## Observed native outcomes

- **Unbound command / Metadata fallback:** persisted tolerance **1234 ms**, source
  type `Album`, analysis enabled, absolute decoder path. One report-only exact
  duplicate, zero reviews, no unmonitor/deletion. The default-definition regression
  failed at 3000 ms before the fix and passed afterward. Real dispatch confirms
  the persisted values in the cleanup-service log.
- **Cache and stale content:** repeated unchanged scan reports one duplicate;
  replacing the retained FLAC with `late-sample.flac` after warming the cache yields
  one unresolved review and no cleanup, even with exact cleanup enabled. Restoring
  it restores eligibility. A deliberately absent decoder yields unavailable evidence,
  one review and preserved files. Command success alone is not audio success.
- **Connect precedence:** Metadata deliberately names an absent decoder and enables
  cleanup; Connect supplies the real decoder, tolerance **2345 ms** and report-only
  analysis. The scan uses Connect and reports without cleanup. Thus this also checks
  that Metadata cannot silently add cleanup authority over Connect.
- **Self/source protection:** `Single` as comparison type cannot use the candidate
  itself. An unmonitored retained source cannot justify cleanup. Both cases preserve
  the candidate. A catalog-only exclusive B-side produces a whole-release keep,
  zero reviews and zero cleanup; its track evidence remains visible.
- **Real import parity:** actual `ManualImport` dispatch copies a synthetic FLAC
  through the host importer and invokes `UniqueSinglesNotification`'s
  `single-self-check` route. Report-only import logs `reportOnlyExactDuplicates=1`,
  `cleaned=0`, `reviewNeeded=0`; the candidate stays monitored and its imported file
  exists. With explicit cleanup enabled, another real import logs `cleaned=1`,
  unmonitors the candidate and removes its actual imported file. Source monitoring
  and source file remain intact. The runner asserts handler logs and file/repository
  outcomes because Lidarr can report a completed import command even when skipping it.
- **Explicit scan cleanup:** Connect's cleanup toggle permits one whole-release
  cleanup after verified coverage and immediate revalidation before unmonitor and
  deletion. Candidate file disappears; retained source stays monitored and present.
- **Survivor:** two new equivalent synthetic singles, with `Single` comparison and
  explicit cleanup, produce exactly one cleanup and leave exactly one monitored
  single and one existing counterpart file. They cannot both justify removal.
- **Scheduling/restart:** disabled Metadata creates no plugin task. Enabling creates
  interval **10080**. After command completion, last execution
  `2026-10-28T13:46:51Z` and next execution `2026-11-04T13:46:51Z` survive a native
  process restart unchanged. Disabling removes the task. Restart does not make it
  immediately overdue.

## Regression and archive gates

**248 C# tests passed, zero failed/skipped; five Python archive-preparation tests
passed.** Release restore/build and `git diff --check` passed. Existing compiler
nullable/analyzer warnings remain on a clean compilation; no warning suppression
or test skip was introduced.

`PluginIdentitySmokeTests.cs` was excluded from compilation despite being valid
against the supported host. Restoring it produced a failing missing-archive test:
Release builds did not generate its claimed ZIP. The Release target now produces
only top-level DLL/PDB entries, the identity/package tests compile and pass, and
CI publishes that tested ZIP instead of constructing a different archive afterward.

The scan/import cleanup-service regressions use real synthetic FLAC plus recording
repository/media-service doubles. They cover both single and album import routes,
full multi-track coverage, alternate/exclusive/missing tracks, configured/excluded/
unmonitored sources, self/survivor protections, forged checksums, late samples,
corruption/truncation, incompatible formats, content/decoder cache invalidation,
budgets/concurrency/cancellation and source/candidate changes between verification,
unmonitor and deletion. Failure handling preserves remaining files and accounts for
unmonitor/delete failures. A new failing all-routes regression proved that an
undownloaded exclusive B-side was omitted from release counts after incomplete
analysis; the fix uses whole-catalog evidence for that keep decision without adding
cleanup authority or changing the no-file Tier-1 path.

## Remaining boundaries

This qualifies the specified **macOS arm64 host and exact decoder build**. Linux,
Windows, other FFmpeg builds, production images, accounts/permissions and real
libraries were not qualified. No production action is implied. Decoder executable
hashing does not attest its shared libraries; restart/requalify after deployment
changes. Packaging/license obligations and the supported 16/24-bit FLAC scope,
32-pair operation budget, 30-second pair deadline, size bounds and process-local
cache are documented in [operations](../exact-flac-analysis.md).

Native import proof above is the single-import route. Album-import parity,
mid-operation mutations, failure injection, cancellation and every secondary-type
exclusion are proved at the specified cleanup-service seam, not by injecting faults
into the native host. OS file/repository operations are not atomic; external changes
after the final check remain a race. Synchronous host entry points still do not
supply dispatcher cancellation tokens. No performance benchmark is claimed.

Tracker publication, issue closure, push and production deployment were not performed.
