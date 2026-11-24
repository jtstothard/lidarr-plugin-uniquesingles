# Issue #49: Windows host qualification of the exact-audio path

Ticket: [#49](https://github.com/jtstothard/lidarr-plugin-uniquesingles/issues/49), the
Windows criterion. Run date: 2026-11-24. Native run directory
`_temp/issue49-108c63efb1394c108b07e16791e5a131`: **24 stages, `completed=true`, runner exit 0**.
Receipts: [host results](issue-49-windows-host.json), [environment and gates](issue-49-windows-environment.json).

## Identity

- Host: Jay's Windows desktop, Windows 11 (NT 10.0.26200.0) x64, AMD Ryzen 7 9800X3D,
  interactive user session. Nothing was installed system-wide; the run used a user-local
  checkout, Python 3.14.5 and the pinned FFmpeg, with PATH changed only for the run's process.
- Runner and plugin commit `1a2beae` (branch `t3/inbox-windows-exact-audio-qual`), built with
  .NET SDK 8.0.419. Plugin DLL SHA-256
  `bda075065cb935ff66f5456df1f96ebd4a244f2defc4e68d53e3fad83fe88faf`.
- Native host **Lidarr 3.1.3.4968** (`Lidarr.Console.exe`, bundled .NET 8.0.27) from
  `Lidarr.develop.3.1.3.4968.windows-core-x64.zip`, pinned SHA-256
  `4bd64517008f319d37d4d4585146a474ac2ff2d0183b1b751661f89f2b434280`.
- Decoder: FFmpeg **9.0.2**, gyan.dev essentials build from
  `GyanD/codexffmpeg` release `9.0.2`, archive `ffmpeg-9.0.2-essentials_build.zip`
  (SHA-256 `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`, matching the
  release's published digest). `ffmpeg.exe` SHA-256
  `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`.

## Gates

All on the same host, in order: `test_prepare_lidarr.py` (6 OK), `prepare-lidarr.py` (458 verified
assemblies), Release build (0 errors), `dotnet test` (577 passed, 0 failed, 0 skipped), then
`python -X utf8 scripts/qualify-disposable-host.py`. The `-X utf8` flag makes Python read the
host's UTF-8 log in UTF-8 instead of the Windows code page.

## Stages

The same stages as the [macOS run](issue-49.md), plus `disabled-entry-skips-scan`. All passed:
report-only and warm-cache scans, changed-source and absent-decoder rejection, Connect precedence,
self-source and unmonitored-source protection, exclusive-content keep, the manual-import report
and opt-in routes, explicit Connect cleanup, scheduled opt-in with restart persistence, the
disabled-entry skip, survivor retention, native album-import cleanup through the real dispatcher,
mid-analysis mutation, decode failure, decoder crash, unmonitor-failure injection and recovery,
delete-failure injection, shutdown cancellation mid-decode, and cancellation recovery.

## How the Windows run differs (stated, not glossed)

The runner's POSIX fault mechanisms have Windows equivalents. Each preserves the stage's assertion
but is not the identical OS event:

- **Decoder wrappers** are `.cmd` files run by .NET's `Process.Start` through `cmd.exe`, with the
  decoder's stdin/stdout piped through, in place of `#!/bin/sh` scripts.
- **Mid-analysis mutation.** The verifier holds the file it is decoding open without write
  sharing, so on Windows the wrapper's copy fails during the candidate's own decode. It lands
  during the source's decode instead, still before the post-decode identity recheck, and the
  release is preserved with `identity-changed-during-analysis`. On POSIX the first copy succeeds.
  Windows share enforcement blocks an external in-place writer while the file is open. The
  POSIX-only post-delete mutation proofs in the C# suite stay guarded off on Windows by design.
- **Decoder crash.** Windows has no signals: the wrapper's `cmd.exe` is terminated externally
  (`Stop-Process -Force`, i.e. TerminateProcess) in place of `kill -9 $$`. Outcome
  `status=Unavailable`, release preserved.
- **Delete failure.** A read-only directory does not block deletion on Windows. The runner instead
  holds the candidate open without delete sharing during the scan. The release stays unmonitored
  with its file present, `1 delete failures`, `reason=delete-failed`.
- **Shutdown.** Windows has no SIGTERM: every stop asks the host to shut down through
  `POST /api/v1/system/shutdown`, with TerminateProcess only as a fallback after 60 s. No stop
  needed the fallback (`forcedStops` absent). The shutdown-cancellation stage observed
  `cancelled-or-time-budget` and `dispatcher-shutdown`. The decoder had started, nothing was
  deleted, and the next scan cleaned.

## Runner changes in this run

- Windows support (commit `e2fa6a1`): the RID and zip archive, the console host, API shutdown,
  `.cmd` wrappers, and the open-handle delete failure. POSIX behaviour is unchanged.
- Two fixes for behaviour added after the 2026-11-03 run (commit `1a2beae`). Without them the
  runner failed at its first scan on every platform. Scans now refuse while the Metadata entry is
  disabled (#68), so the runner enables the entry after checking that a disabled entry registers
  no scheduled task, and it adds a stage proving the disabled entry skips. An API re-monitor is a
  user re-monitor the plugin honours (#71), so the opt-in import stage resets the disposable
  candidate in the database instead of re-monitoring through the API.
- With both commits, the runner also passed all 24 stages on macOS 26.3.1 arm64 with Homebrew
  FFmpeg 9.0.2 on the same day (`_temp/issue49-64c5035ba93b49b18433798b1e198bf6`).

## Boundaries

This qualifies the exact-audio path on Windows 11 x64 with this pinned FFmpeg 9.0.2 build,
the pinned Lidarr 3.1.3.4968 Windows host and this plugin build, in a disposable host with a
synthetic library. It does not qualify other FFmpeg builds, Windows on arm64, a Windows service
install, network shares or a production Lidarr.
