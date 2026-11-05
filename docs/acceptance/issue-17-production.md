# PR #54 production deployment — 2026-11-05

Deployment of the PR #54 build (**1.0.2.119**, merged main
`cb9fb176a03f4b5ebaa9d36f38093bd983be659c` at 2026-11-05T13:44:51Z, issue #17) completed
on Proxmox **192.168.10.51** VM102 (`media-vm`), Docker container `lidarr`, at
**2026-11-05T13:55:44Z** via a clean container restart (13:55:30Z). No scan was
triggered during the deploy session and no settings were changed; the existing
schedule remains the trigger for running the new code.

## Trusted source and runtime

- Merged main: `cb9fb176a03f4b5ebaa9d36f38093bd983be659c` (PR #54, issue #17).
- Main push [CI run 42038505174](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/42038505174),
  run number 119, **Build Plugin** passed (event `push`, head `cb9fb17`).
  Its GitHub release **1.0.2.119**, created **2026-11-05T13:53:31Z**, was
  downloaded through authenticated GitHub CLI; no local `_temp` build was trusted.
- Release status note: **1.0.2.119 is a published release** (`isDraft=false`, tagged
  `1.0.2.119`, marked **Latest**) — the first build published by the new
  publish-on-main workflow from PR #51. Unlike 1.0.2.106, no draft step was involved.
- Release ZIP SHA-256: `48f5f09caf689c0e98a23ecb57242665877f44066b47929a1fe6632166295a1d`
  (matches the GitHub asset digest byte-for-byte).
- Deployed DLL SHA-256: `adde1ddff07a370918c810149a2dc4a75ae35b9ebea6f6d3595ccc01f3d1844a`
  (122,368 bytes).
- Deployed PDB SHA-256: `f4e4e2d7e1f3f53ef4696a578966930374051d859263b652e3270eb50e9a209f`
  (34,388 bytes).
- Runtime: the LinuxServer Lidarr image in container `lidarr`, bundled Lidarr
  **3.2.0.5175**, account **99:100**.
- Plugin path: `/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-uniquesingles/`
  (host config bind for the container's `/config`). Other plugins, container mounts
  and unrelated services were preserved.
- Pinned decoder unchanged: `/config/exact-audio-decoder/bin/ffmpeg`, Alpine FFmpeg
  **8.1.3-r0** from the PR #43 deployment. Nothing under the decoder tree was touched.

## What this build delivers

PR #54 (issue #17) delivers one thing:

- The metadata cleanup path now evaluates **all catalog tracks** when deciding whether
  a release is safe to clean. An undownloaded catalog track without a Tier-1/Tier-2
  redundant match protects the release — keep-exclusive semantics, with no file
  deletion and no unmonitor for such releases. The exact-audio path was already
  strict and is unchanged.

Existing protections are unchanged: the delete-time mutation guard, revalidation
before unmonitor and every deletion, survivor/exclusive-content protections, Tier-3
`FlagOnly` semantics and report-only authority.

Test evidence: the suite stands at **301/301**, including **5 new tests** covering the
undownloaded-exclusive-track protection.

## Backup before swap

Before replacing the binaries, a root-only rollback bundle was created at
`/var/tmp/uniquesingles-deploy-b37b1c3f/` and integrity-verified before the swap. It
contains:

- `plugin-before-1.0.2.106.dll`
- `plugin-before-1.0.2.106.pdb`
- `receipt.json`

The previous build, for the rollback record: **1.0.2.106 = `9605fbd`**, DLL SHA-256
`147e00dd4146fa41a5ef3698f1f0341584eca4c1de220de240c7d6a84969b7cb`. The earlier
`/var/tmp/uniquesingles-deploy-f2dd730b`, `/var/tmp/uniquesingles-deploy-43d344ec` and
`/var/tmp/uniquesingles-deploy-91749a4` bundles were left untouched.

The new DLL/PDB were installed by same-directory atomic replace, preserving the
existing **99:100** ownership and **664/644** file modes.

## Settings unchanged

This deployment changed no settings. Pre/post snapshots of both providers — **Connect
(notification) ID 54** and **Metadata ID 15** — were compared and are identical across
all seven fields:

| Setting | Before | After |
| --- | --- | --- |
| exactAudioAnalysis | true | true |
| exactAudioCleanup | true | true |
| exactAudioDecoderPath | `/config/exact-audio-decoder/bin/ffmpeg` | `/config/exact-audio-decoder/bin/ffmpeg` |
| tier3Action | `flagOnly` | `flagOnly` |
| releaseTypesToCheck | `Album, EP` | `Album, EP` |
| durationToleranceMs | 3000 | 3000 |
| scanIntervalMinutes | 1440 | 1440 |

No provider was recreated or updated through the API in this session.

## Post-deploy verification

All checks passed after the 13:55:30Z restart:

- `/system/status` returned 200 with process start time **2026-11-05T13:55:44Z**.
- `/system/plugins` reports installed **1.0.2.119** for Unique Singles
  (`installedVersion` 1.0.2.119, `availableVersion` 1.0.2.119, `updateAvailable`
  false).
- On-disk DLL and PDB SHA-256 hashes byte-match the GitHub release artifact.
- The **Unique Singles Scan** task is present with interval **1440 minutes**,
  last execution **2026-11-05T13:14:10Z** and preserved next execution
  **2026-11-06T13:14:10Z**.
- Settings are intact and identical on both providers (table above).
- `logs.db` shows a clean plugin startup after the restart: 0 errors; scheduler
  registered `UniqueSinglesScanCommand interval=1440min nextRunDue=2026-11-06
  13:14:10Z`.

Production has **not run a scan with the new code during this deployment**; the
scheduled **2026-11-06T13:14:10Z** scan will be the first to execute build 1.0.2.119.
No scan was forced.

## Rollback

Rollback was not needed. To revert, replace the deployed DLL/PDB with the bundle
copies in `/var/tmp/uniquesingles-deploy-b37b1c3f/` (same-directory atomic replace
keeps ownership and modes) and restart the container; Lidarr then reports **1.0.2.106**.
No settings change is required for rollback — the previous build already ran with
exactly these values.

Validation: successful main CI build (run number 119), published release with ZIP
digest match, deployed-binary hash match against the GitHub artifact, API
status/plugin checks, preserved schedule, provider snapshot equality and a clean
plugin startup in `logs.db`. The deployment was binary-plus-restart only: no settings
change, no decoder or image change, no database modification and no unrelated service
change was performed.
