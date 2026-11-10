# PR #56 production deployment — 2026-11-10

Deployment of the PR #56 build (**1.0.2.123**, squash-merged main
`84dfb445490517c6ae8b6c8376764a1f30678670` at 2026-11-10T14:57:45Z, closing
issue #26) completed on Proxmox **192.168.10.51** VM102 (`media-vm`), Docker
container `lidarr`, at **2026-11-10T15:53:03Z** via a clean container restart
(15:52:53Z). No scan was triggered during the deploy session and no settings
were changed; the existing schedule remains the trigger for running the new
code.

## Trusted source and runtime

- Merged main: `84dfb445490517c6ae8b6c8376764a1f30678670` (PR #56, issue #26).
- Main push [CI run 42839433259](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/42839433259),
  run number 123, **Build Plugin** passed (event `push`, head `84dfb44`).
  Its GitHub release **1.0.2.123**, created **2026-11-10T15:04:05Z**,
  published (not draft) and marked **Latest**, was downloaded through
  authenticated GitHub CLI; no local `_temp` build was trusted.
- Release ZIP SHA-256: `489f5c4dc3da2ef3b34ccf210f2091e25a1fab515d150d61b710f65e14407dc9`
  (matches the GitHub asset digest, byte-for-byte).
- Deployed DLL SHA-256: `7b177ee5b5e00af9f6084f84318f58357f1eea77b5b9c62e4fcf2d06e8c3de5a`
  (132,096 bytes).
- Deployed PDB SHA-256: `10b5c4108fc2720bf7644f949be28e130b3f61c604820d6ed97113147874f8a1`
  (36,216 bytes).
- Runtime: the LinuxServer Lidarr image in container `lidarr`, bundled Lidarr
  **3.2.0.5175**, account **99:100**.
- Plugin path: `/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-uniquesingles/`
  (host config bind for the container's `/config`). Other plugins, container
  mounts and unrelated services were preserved.
- Pinned decoder unchanged: `/config/exact-audio-decoder/bin/ffmpeg`, Alpine
  FFmpeg **8.1.3-r0**. Nothing under the decoder tree was touched.

## What this build delivers

PR #56 (issue #26) delivers eager pre-grab unmonitoring through
`EagerSingleUnmonitorService`:

- Metadata-refresh lifecycle hooks (`AlbumInfoRefreshed`, `AlbumAdded`,
  `AlbumUpdated`, `AlbumEdited`) unmonitor fully-redundant, file-less,
  monitored singles **before** they can be grabbed, ending the
  download-then-delete churn this plugin previously only cleaned up after the
  fact.
- The `OnGrab` hook was deliberately rejected: it fires post-dispatch, after
  Lidarr has already handed the release to the download client, so acting
  there would leak files. The reasoning is documented in
  `docs/eager-unmonitor.md`.
- The #17 all-catalog-tracks protection is preserved unchanged: any exclusive
  or unmatched catalog track keeps the single monitored.
- The logic triggers no searches and no grabs of its own.

Test evidence: **326/326** suite (25 new). Existing protections are
unchanged: revalidation before unmonitor and every deletion,
survivor/exclusive-content protections, Tier-3 `FlagOnly` semantics and
report-only authority.

## Backup before swap

Before replacing the binaries, a root-only rollback bundle was created at
`/var/tmp/uniquesingles-deploy-ccaa856c/` and integrity-verified before the
swap. It contains:

- `plugin-before-1.0.2.119.dll`
- `plugin-before-1.0.2.119.pdb`
- `receipt.json`

The previous build, for the rollback record: **1.0.2.119 = `cb9fb17`**, DLL
SHA-256 `adde1ddff07a370918c810149a2dc4a75ae35b9ebea6f6d3595ccc01f3d1844a`.
The earlier `/var/tmp/uniquesingles-deploy-b37b1c3f`,
`/var/tmp/uniquesingles-deploy-f2dd730b`,
`/var/tmp/uniquesingles-deploy-43d344ec` and
`/var/tmp/uniquesingles-deploy-91749a4` bundles were left untouched.

The new DLL/PDB were installed by same-directory atomic replace, preserving
the existing **99:100** ownership and **664/644** file modes.

## Settings: seven fields unchanged, one new field observed

This deployment changed no settings. Pre/post snapshots of both providers —
**Connect (notification) ID 54** and **Metadata ID 15** — were compared and
are identical across all seven pre-existing fields:

| Setting | Before | After |
| --- | --- | --- |
| exactAudioAnalysis | true | true |
| exactAudioCleanup | true | true |
| exactAudioDecoderPath | `/config/exact-audio-decoder/bin/ffmpeg` | `/config/exact-audio-decoder/bin/ffmpeg` |
| tier3Action | `flagOnly` | `flagOnly` |
| releaseTypesToCheck | `Album, EP` | `Album, EP` |
| durationToleranceMs | 3000 | 3000 |
| scanIntervalMinutes | 1440 | 1440 |

The new field delivered by this build, `eagerUnmonitorRedundantSingles`, was
observed present on **both** providers after the restart, at its default
value **true** — it was not modified in this session. No provider was
recreated or updated through the API.

## Post-deploy verification

All checks passed after the 15:52:53Z restart:

- `/system/status` returned 200 with process start time **2026-11-10T15:53:03Z**.
- `/system/plugins` reports installed **1.0.2.123** for Unique Singles,
  equal to the available version with `updateAvailable` false.
- On-disk DLL and PDB SHA-256 hashes byte-match the GitHub release artifact.
- The **Unique Singles Scan** task is present with interval **1440 minutes**,
  last execution **2026-11-10T14:18:54Z** and preserved next execution
  **2026-11-11T14:18:54Z**.
- Settings are intact and identical on both providers (table above), with the
  new `eagerUnmonitorRedundantSingles` field at its default on both.
- `logs.db` shows a clean plugin startup after the restart: 0 errors;
  scheduler registered `nextRunDue` **2026-11-11 14:18:54Z**.

Production has **not run a scan with the new code during this deployment**;
the scheduled **2026-11-11T14:18:54Z** scan will be the first scheduled pass
to execute build 1.0.2.123. No scan was forced. The eager unmonitor path
itself first arms on the next album metadata refresh or add.

## Rollback

Rollback was not needed. To revert, replace the deployed DLL/PDB with the
bundle copies in `/var/tmp/uniquesingles-deploy-ccaa856c/` (same-directory
atomic replace keeps ownership and modes) and restart the container; Lidarr
then reports **1.0.2.119**. No settings change is required for rollback — the
previous build already ran with exactly these seven values, and the extra
`eagerUnmonitorRedundantSingles` field is simply ignored by the old binary.

Validation: successful main CI build (run number 123), published-release ZIP
digest match, deployed-binary hash match against the GitHub artifact, API
status/plugin checks, preserved schedule, provider snapshot equality (plus
the new field observed at default on both providers) and a clean plugin
startup in `logs.db`. The deployment was binary-plus-restart only: no
settings change, no decoder or image change, no database modification and no
unrelated service change was performed.
