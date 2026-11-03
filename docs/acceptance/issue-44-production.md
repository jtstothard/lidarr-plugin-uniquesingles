# PR #47 production deployment — 2026-11-03

Follow-up deployment of the PR #47 build (**1.0.2.102**, merged main
`bcbccf0351d28bc0a5ba1ec3df74219cd652d333`, closing issue #44) completed on
Proxmox **192.168.10.51** VM102 (`media-vm`), Docker container `lidarr`, at
**2026-11-03T13:48:00Z** via a clean container restart. No scan was triggered
during the deploy session and no settings were changed; the existing schedule
remains the trigger for running the new code.

## Trusted source and runtime

- Merged main: `bcbccf0351d28bc0a5ba1ec3df74219cd652d333` (PR #47, issue #44).
- Main push [CI run 41644598856](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/41644598856),
  run number 102, **Build Plugin** passed (event `push`, head `bcbccf0`).
  Its GitHub draft release **1.0.2.102**, created **2026-11-03T13:29:08Z**, was
  downloaded through authenticated GitHub CLI; no local `_temp` build was trusted.
- Release ZIP SHA-256: `164cd8c0d8347c5dbc23bdce73a12d0944898070a6574462a0260ced8d60f691`
  (matches the GitHub asset digest).
- Deployed DLL SHA-256: `e204639da55a759ac256ca544686cc5180ffb9143996df5f594a26a654a9febb`.
- Deployed PDB SHA-256: `ca07a177b4600ebbda654b2bc32682f4cf0418c5e1a833fddeea86e741b983d2`.
- Runtime: the `lscr.io/linuxserver/lidarr:nightly` image in container `lidarr`,
  bundled Lidarr **3.2.0.5164**, account **99:100**. The nightly image had
  self-updated **3.2.0.5150 → 3.2.0.5164** earlier the same day (approximately
  04:03Z), before and unrelated to this deployment; the deploy restart itself
  was clean.
- Plugin path: `/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-uniquesingles/`
  (host config bind for the container's `/config`). Other plugins, container
  mounts and unrelated services were preserved.
- Pinned decoder unchanged: `/config/exact-audio-decoder/bin/ffmpeg`, Alpine
  FFmpeg **8.1.3-r0** from the PR #43 deployment. Nothing under the decoder
  tree was touched.

## What this build delivers

PR #47 (issue #44) follow-ups to the exact-audio matching delivery (PR #43):

- Truthful `cleanupAuthority` on exact-audio release lines (plus a
  `reviewPriority` label) instead of a hard-coded literal; actual destructive
  grants stay on the `decision=revalidated` lines.
- `keepExclusive` summary aggregate so exclusive-content keeps are counted
  separately instead of folded into `skipped`, letting summaries reconcile
  with per-release `outcome=` lines, and completion wording clarifying that
  `cleaned` counts releases unmonitored (deletion not guaranteed).
- Dispatcher cancellation wiring into scan/import audio analysis
  (`AudioAnalysisCancellation` hub), so a scan stops before the next artist
  and decoder work returns unavailable evidence; settings precedence is
  unchanged.
- Review-priority comparison budgeting: 64 total comparisons = 32 shared +
  32 reserved for review-needed candidates, which are also ordered first, so
  earlier candidates can no longer starve the review queue.
- Stable decode-failure reason tokens (for example
  `streaminfo-digest-disagreement`) without logging stderr or paths.
- Lidarr History audit rows written for plugin unmonitors; file deletions
  already appear as Lidarr's own `TrackFileDeleted` history events because
  cleanup deletes through Lidarr's deletion service. History write failures
  never alter a cleanup decision.

Existing protections are unchanged: revalidation before unmonitor and every
deletion, survivor/exclusive-content protections, Tier-3 `FlagOnly`
semantics and report-only authority.

## Backup before swap

Before replacing the binaries, a root-only rollback bundle was created at
`/var/tmp/uniquesingles-deploy-43d344ec/` (mode 0700) and verified
(`BACKUP_VERIFIED`) before the swap. It contains:

- `plugin-before-1.0.2.98.dll`
- `plugin-before-1.0.2.98.pdb`
- `receipt.json`

The previous build, for the rollback record: **1.0.2.98 = `f7eaee6`**, DLL
SHA-256 `575c89be0cd881f3c75f2a0fbf2efbbf7794bbabc5b49030786c7dcfeb11b46d`.
The earlier `/var/tmp/uniquesingles-deploy-91749a4` bundle was left untouched.

The new DLL/PDB were installed by same-directory atomic replace, preserving
the existing **99:100** ownership and **664/644** file modes.

## Settings unchanged

This deployment changed no settings. Pre/post snapshots of both providers —
**Connect (notification) ID 54** and **Metadata ID 15** — were compared and
are identical across all seven fields:

| Setting | Before | After |
| --- | --- | --- |
| exactAudioAnalysis | true | true |
| exactAudioCleanup | true | true |
| exactAudioDecoderPath | `/config/exact-audio-decoder/bin/ffmpeg` | `/config/exact-audio-decoder/bin/ffmpeg` |
| tier3Action | `FlagOnly` | `FlagOnly` |
| comparisonReleaseTypes | `Album,EP` | `Album,EP` |
| durationToleranceMs | 3000 | 3000 |
| scanIntervalMinutes | 1440 | 1440 |

No provider was recreated or updated through the API in this session.

## Post-deploy verification

All checks passed after the 13:48:00Z restart:

- `/system/status` returned 200 with process start time **2026-11-03T13:48:00Z**.
- `/system/plugins` reports installed **1.0.2.102** for Unique Singles.
- On-disk DLL and PDB SHA-256 hashes byte-match the GitHub release artifact.
- The **Unique Singles Scan** task is present with interval **1440 minutes**
  and preserved next execution **2026-11-04T12:45:34Z**.
- Settings are intact and identical on both providers (table above).
- `logs.db` shows a clean plugin startup: scheduler registered, no plugin
  exceptions.

Production has **not run a scan with the new code during this deployment**;
the scheduled **2026-11-04T12:45:34Z** scan will be the first to execute
build 1.0.2.102. No scan was forced.

## Rollback

Rollback was not needed. To revert, replace the deployed DLL/PDB with the
bundle copies in `/var/tmp/uniquesingles-deploy-43d344ec/` (same-directory
atomic replace keeps ownership and modes) and restart the container; Lidarr
then reports **1.0.2.98**. No settings change is required for rollback — the
previous build already ran with exactly these values.

Validation: successful main CI build (run number 102), release ZIP digest
match, deployed-binary hash match against the GitHub artifact, API
status/plugin checks, preserved schedule, provider snapshot equality and a
clean plugin startup in `logs.db`. The deployment was binary-plus-restart
only: no settings change, no decoder or image change, no database
modification and no unrelated service change was performed.
