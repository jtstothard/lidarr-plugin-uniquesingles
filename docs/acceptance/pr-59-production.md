# PR #59 production deployment — 2026-11-21

Deployment of **1.0.2.128** (PR #59, squash-merged main
`1ecc675d4d6bbd408171e30babb1335e99ff7996`) on Proxmox **192.168.10.51** VM102
(`media-vm`), Docker container `lidarr`, by binary swap plus a container restart at
**2026-11-21T18:24:53Z** (Lidarr start time 18:24:56Z). No scan was triggered and no
settings were changed.

## Trusted source and runtime

- Main push [**Build Plugin** run 44935043513](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/44935043513),
  run number 128 (event `push`, head `1ecc675`), passed; its run number is what makes
  the version 1.0.2.128. GitHub release **1.0.2.128** (published, tag → `1ecc675`) was
  downloaded with authenticated GitHub CLI. No local build was deployed.
- *Correction (2026-11-24):* this receipt originally cited "Main push CI run 141
  (`44935043528`)" as the trusted source. That is the **CodeQL** workflow run for the
  same commit (CodeQL has its own run counter), not the build that produced the
  release. The Build Plugin run above is the build provenance.
- Release ZIP SHA-256 `00d3955f974e3f25003ae11278486d855988487089fabf179902f2deba7e68e1`
  (matches the GitHub asset digest).
- Deployed DLL SHA-256 `c4519d63bb4b796bb7f883ac84e28bfafd820be72263f47234cff81610820151`
  (132,608 bytes); PDB `b994fb8cb606575310a6424a993ac87df876063808532c6062161b4d13988a84`
  (36,288 bytes). Both byte-match the ZIP members.
- Runtime: LinuxServer Lidarr **4.0.0.5227** on .NET **10.0.15**, account 99:100. CI still
  references Lidarr 3.1.3.4968 / .NET 8; the previous build (1.0.2.125, receipt backfilled
  in [build-125-production.md](build-125-production.md)) already ran its
  scheduled scan on this runtime on 2026-11-21, and #59 uses no new Lidarr APIs.
- Plugin path `/srv/docker-state/stacks/media/lidarr/plugins/jtstothard/lidarr-plugin-uniquesingles/`;
  files installed by same-directory atomic rename, 99:100, modes 664/644 preserved.

## What this build delivers

The scheduled scan and album-import cleanup apply the eager Tier-1/Tier-2 rule to monitored
singles with no files (when **Eagerly unmonitor redundant singles** is on). Before deploy,
25 such singles (every track downloaded on a monitored album, matched by title + duration
only) had been unmonitored by hand on 2026-11-21; they are the production case this fixes.

## Backup before swap

`/var/tmp/uniquesingles-deploy-5OEWRNHf/` (root only): `plugin-before-1.0.2.125.dll`
(SHA-256 `2c9426dc45725ce9fc826f43b7cfe12c95c3f3b271c321bea0b9d585aa5478ec`),
`plugin-before-1.0.2.125.pdb` (`6b3515c044340ec98b935d81d2266bae1186f94316c2908133a76af8e389f569`)
and `receipt.json`; both copies were hash-verified before the swap.

## Post-deploy verification

- `/system/status` 200, start time 2026-11-21T18:24:56Z; `/system/plugins` reports
  Unique Singles **1.0.2.128**.
- Connect ID 54 and Metadata ID 15: all eight fields identical before and after
  (`durationToleranceMs` 3000, `eagerUnmonitorRedundantSingles` true, `exactAudioAnalysis`
  true, `exactAudioCleanup` true, decoder `/config/exact-audio-decoder/bin/ffmpeg`,
  `releaseTypesToCheck` "Album, EP", `scanIntervalMinutes` 1440, `tier3Action` flagOnly).
- **Unique Singles Scan** task: interval 1440, last 2026-11-21T17:07:12Z, next
  2026-11-22T17:07:12Z (preserved). That scan is the first to run the new code.
- Startup log: scheduler registered the scan; no errors. The "No compatible release found …
  framework net10.0" warnings appear for every installed plugin and come from Lidarr's
  update check, not plugin loading.

## Rollback

Copy the two bundle files back over the plugin DLL/PDB (same-directory atomic replace) and
restart the container; Lidarr then reports 1.0.2.125. No settings change is needed.
