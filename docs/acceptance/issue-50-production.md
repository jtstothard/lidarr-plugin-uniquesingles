# PR #50 production deployment — 2026-11-03

Deployment of the PR #50 build (**1.0.2.106**, merged main
`9605fbd5cbaa41feb20565d8ff428aeae2a8bde7` at 2026-11-03T18:57Z, issue #49) completed
on Proxmox **192.168.10.51** VM102 (`media-vm`), Docker container `lidarr`, at
**2026-11-03T19:08:24Z** via a clean container restart (19:08:17Z). No scan was
triggered during the deploy session and no settings were changed; the existing
schedule remains the trigger for running the new code.

## Trusted source and runtime

- Merged main: `9605fbd5cbaa41feb20565d8ff428aeae2a8bde7` (PR #50, issue #49).
- Main push [CI run 41698697793](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/41698697793),
  run number 106, **Build Plugin** passed (event `push`, head `9605fbd`).
  Its GitHub draft release **1.0.2.106**, created **2026-11-03T19:00:00Z**, was
  downloaded through authenticated GitHub CLI; no local `_temp` build was trusted.
- Release ZIP SHA-256: `faf43ec31882f80674d9231285d5bc4da907e44aadff59e0667d6316c6be4d8d`
  (matches the GitHub asset digest).
- Deployed DLL SHA-256: `147e00dd4146fa41a5ef3698f1f0341584eca4c1de220de240c7d6a84969b7cb`
  (122,368 bytes).
- Deployed PDB SHA-256: `bf8e6570bb68b5834f32f1405d4d54420618d75fd29fadff58ea4bd0786f6cc6`
  (34,392 bytes).
- Release status note: **1.0.2.106 is still a draft release** on GitHub at the time of
  writing; the publish-on-main workflow fix is in flight as PR #51. The artifact was
  fetched from the draft release through authenticated GitHub CLI, so the draft status
  does not affect the provenance above.
- Runtime: the LinuxServer Lidarr image in container `lidarr`, bundled Lidarr
  **3.2.0.5164**, account **99:100**.
- Plugin path: `/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-uniquesingles/`
  (host config bind for the container's `/config`). Other plugins, container mounts
  and unrelated services were preserved.
- Pinned decoder unchanged: `/config/exact-audio-decoder/bin/ffmpeg`, Alpine FFmpeg
  **8.1.3-r0** from the PR #43 deployment. Nothing under the decoder tree was touched.

## What this build delivers

PR #50 (issue #49) delivers two things:

- The delete-time mutation guard: exact-audio deletions are now anchored on an open
  `FileShare.Read | FileShare.Delete` handle. The verified content digest is re-checked
  through that handle before deletion, the handle is held across Lidarr's deletion
  call, and the pinned bytes are re-read afterwards. In-place mutation is blocked
  outright or detected after the fact with a fail-closed
  `mutation-detected-post-delete` outcome; the rename-over TOCTOU window is documented
  as inherent to the platform.
- Native album-import fault-stage qualification: **23/23** stages (receipt:
  `docs/acceptance/issue-49.md`), with the test suite at **294/294**.

Existing protections are unchanged: revalidation before unmonitor and every deletion,
survivor/exclusive-content protections, Tier-3 `FlagOnly` semantics and report-only
authority.

## Backup before swap

Before replacing the binaries, a root-only rollback bundle was created at
`/var/tmp/uniquesingles-deploy-f2dd730b/` and integrity-verified before the swap. It
contains:

- `plugin-before-1.0.2.102.dll`
- `plugin-before-1.0.2.102.pdb`
- `receipt.json`

The previous build, for the rollback record: **1.0.2.102 = `bcbccf0`**, DLL SHA-256
`e204639da55a759ac256ca544686cc5180ffb9143996df5f594a26a654a9febb`. The earlier
`/var/tmp/uniquesingles-deploy-43d344ec` and `/var/tmp/uniquesingles-deploy-91749a4`
bundles were left untouched.

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

All checks passed after the 19:08:17Z restart:

- `/system/status` returned 200 with process start time **2026-11-03T19:08:24Z**.
- `/system/plugins` reports installed **1.0.2.106** for Unique Singles.
- On-disk DLL and PDB SHA-256 hashes byte-match the GitHub release artifact.
- The **Unique Singles Scan** task is present with interval **1440 minutes** and
  preserved next execution **2026-11-04T12:45:34Z**.
- Settings are intact and identical on both providers (table above).
- `logs.db` shows a clean plugin startup after the restart: 0 errors; scheduler
  registered `UniqueSinglesScanCommand interval=1440min nextRunDue=2026-11-04
  12:45:34Z`.

Production has **not run a scan with the new code during this deployment**; the
scheduled **2026-11-04T12:45:34Z** scan will be the first to execute build 1.0.2.106.
No scan was forced.

## Rollback

Rollback was not needed. To revert, replace the deployed DLL/PDB with the bundle
copies in `/var/tmp/uniquesingles-deploy-f2dd730b/` (same-directory atomic replace
keeps ownership and modes) and restart the container; Lidarr then reports **1.0.2.102**.
No settings change is required for rollback — the previous build already ran with
exactly these values.

Validation: successful main CI build (run number 106), release ZIP digest match,
deployed-binary hash match against the GitHub artifact, API status/plugin checks,
preserved schedule, provider snapshot equality and a clean plugin startup in
`logs.db`. The deployment was binary-plus-restart only: no settings change, no
decoder or image change, no database modification and no unrelated service change
was performed.
