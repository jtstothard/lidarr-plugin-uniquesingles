# Build 1.0.2.181 production deployment — 2026-11-24

Deployment of **1.0.2.181** (main `4dfe9be309d93b7e3e1fc30ac94e104d7f439a11`, PR #83) on
Proxmox **192.168.10.51** VM102 (`media-vm`), Docker container `lidarr`, by binary swap
plus a container restart at **2026-11-24T11:55:20Z** (Lidarr start time 11:55:24Z). It
replaces **1.0.2.128** ([pr-59-production.md](pr-59-production.md)). No scan was
triggered and no settings were changed.

## Trusted source and runtime

- Main push [**Build Plugin** run 45388123607](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/45388123607),
  run number 181 (event `push`, head `4dfe9be`), passed. GitHub release **1.0.2.181**
  (published and marked Latest, tag → `4dfe9be`) was downloaded with an authenticated
  GitHub CLI. No local build was deployed.
- Release ZIP SHA-256 `3e9542f7bea6b3315fa661e3d37784681ff9bf6da161de60f6ecbb3326a6c209`
  (matches the GitHub asset digest; 85,659 bytes).
- Deployed DLL SHA-256 `fdfd90985cbb9b3a895a579fce9b7166cc79d821bcf1f179e9f3b36f5f3acb93`
  (173,056 bytes); PDB `f7579b597470bbacccfde28be8448a4a7627189ace3de0e116bb86c74207e607`
  (43,524 bytes). Both byte-match the ZIP members and were hash-checked on the host
  before the swap.
- Runtime: LinuxServer Lidarr **4.0.0.5236** (nightly) on .NET **10.0.15**, account 99:100.
  The image moved from 4.0.0.5227 (the #59 receipt) between deployments; CI still
  references Lidarr 3.1.3.4968 / .NET 8.
- Plugin path `/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-uniquesingles/`;
  files installed by same-directory atomic rename, 99:100, modes 664/644 preserved.

## What this build delivers

Everything merged since 1.0.2.128: #67 (FFmpeg kill exceptions no longer crash the host),
#68 (scan honours the Enable flag; failed scheduled-task capture fails closed), #69
(receipt chain), #70 (assembly-version build guard), #71 (in-flight/grabbed singles are
no longer unmonitored into a leak; leaked files from automatic grabs are cleaned), #72
(leftover single folders removed safely, including folders stranded by earlier
versions), #73 (docs), #74 (candidate-side secondary-type protection, Tier-1-only
compilation sources, anchored exact-audio deletion), #61 (build properties), #63 (docs),
#82/#83 (CI only: release serialization and Latest guard, artifact action bumps,
pinning policy). The DLL source is identical to 1.0.2.173: `git diff a1c60ae 4dfe9be`
touches only `.github/` and `CHANGELOG.md`.

Expected visible effects at the next scheduled scan: some downloads that leaked into
plugin-unmonitored singles may be cleaned (#71), stranded single folders attributable to
earlier plugin cleanups may be swept to the recycle bin (#72), and files the user fetched
into plugin-unmonitored singles are kept.

## Pre-deploy smoke check (new DI wiring)

The `IInFlightReleaseProbe` / `ISingleFolderCleanup` / `IHistoryService` graph added by
#71/#72 was only unit-tested, so before the swap 1.0.2.173 (same source) was loaded into a
throwaway container on `media-vm`: the production image ID, a fresh empty `/config`,
`--network none`, no media mounts. Results: `/system/plugins` reported Unique Singles
1.0.2.173; both the `UniqueSinglesNotification` (Connect) and `UniqueSinglesScanTask`
(Metadata) definitions were created through the API; the scheduler registered the scan;
a `UniqueSinglesScan` command completed (`0 artists scanned`) with no plugin warning or
error in the log. The only error lines were the expected no-network failures
(server-side notifications, remote plugin lookup). The container and its directory were
removed afterwards.

## Backup before swap

`/var/tmp/uniquesingles-deploy-w3pEPkkW/` (mode 700, root): `plugin-before-1.0.2.128.dll`
(SHA-256 `c4519d63bb4b796bb7f883ac84e28bfafd820be72263f47234cff81610820151`),
`plugin-before-1.0.2.128.pdb` (`b994fb8cb606575310a6424a993ac87df876063808532c6062161b4d13988a84`)
and `receipt.json`; both copies were `cmp`-verified against the live files before the swap.

## Pre-deploy state

No command was queued or running. Lidarr start time 2026-11-24T10:00:09Z; Unique Singles
1.0.2.128.

## Post-deploy verification

- `/system/status` 200, start time 2026-11-24T11:55:24Z; `/system/plugins` reports
  Unique Singles **1.0.2.181**.
- Connect ID 54 and Metadata ID 15: all eight fields identical before and after
  (`durationToleranceMs` 3000, `eagerUnmonitorRedundantSingles` true, `exactAudioAnalysis`
  true, `exactAudioCleanup` true, decoder `/config/exact-audio-decoder/bin/ffmpeg`,
  `releaseTypesToCheck` "Album, EP", `scanIntervalMinutes` 1440, `tier3Action` flagOnly),
  both enabled.
- **Unique Singles Scan** task: interval 1440, last 2026-11-23T17:34:14Z, next
  2026-11-24T17:34:14Z (preserved; the startup log shows
  `nextRunDue=2026-11-24 17:34:14Z`, so the #68 capture path kept the schedule). That
  scan is the first to run the new code.
- Startup log since the restart: the scheduler registered the scan; no plugin warnings or
  errors.

## Rollback

Copy the two backup files back over the plugin DLL/PDB (same-directory atomic replace) and
restart the container; Lidarr then reports 1.0.2.128. No settings change is needed.
