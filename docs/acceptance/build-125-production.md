# Build 1.0.2.125 production deployment — date unrecorded (backfilled receipt)

**This is a backfilled receipt, written 2026-11-24 for chain completeness.** Build
**1.0.2.125** was deployed to Proxmox **192.168.10.51** VM102 (`media-vm`), Docker
container `lidarr`, but the deployment itself was never recorded: no deploy session
receipt, rollback bundle record or settings snapshot from that session exists in this
repository. Everything below is either recoverable from GitHub (marked as such) or
inferred from the PR #59 receipt ([pr-59-production.md](pr-59-production.md)); anything
else is marked **unverifiable** rather than guessed.

## When it was deployed

**Deployment date and time: unverifiable.** The only bounds on the date are:

- not before the release **1.0.2.125** was published, **2026-11-10T16:01:05Z**
  (GitHub release metadata); and
- not after the 1.0.2.128 deployment on **2026-11-21T18:24:53Z**, whose receipt names
  1.0.2.125 as the previous build and records that it "already ran its scheduled scan on
  this runtime on 2026-11-21".

Build 1.0.2.123 was recorded as deployed on 2026-11-10T15:53:03Z
([issue-26-production.md](issue-26-production.md)), which is before 125 existed, so 125
replaced 123 (or a later build, if any other unrecorded build was deployed in between;
the deployed-before state is **unverifiable** because no rollback bundle for the
125 deployment is recorded). The 1.0.2.126 and 1.0.2.127 runs were pull-request builds
(Dependabot and PR #59 branch) and produced no releases.

## Trusted source (GitHub evidence)

- Merged main: `4eea9f83a0e7b0a754bf7dc6c515efcf68154a18`, "Record production
  deployment receipt for PR #56 (1.0.2.123) (#57)", a docs-only merge.
- Main push [CI run 42850205574](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/42850205574),
  run number 125, **Build Plugin** passed (event `push`, head `4eea9f8`, created
  2026-11-10T16:00:01Z).
- GitHub release **1.0.2.125**, published (not draft), created
  **2026-11-10T16:01:05Z**; tag `1.0.2.125` → `4eea9f8`. It was not Latest at the
  time of this audit (1.0.2.130 is).
- Release ZIP `Lidarr.Plugin.UniqueSingles.net8.0.zip`, 69,142 bytes, GitHub asset digest
  SHA-256 `6ace4b039a4c7c658482da72d8f858fc13fb6ca1c0a606acc82cc0d6ca1ffdb1`.
  Whether the deployed ZIP was this exact asset is **unverifiable** (no download record).

## What this build delivers

Nothing new. `git diff 84dfb44 4eea9f8` (1.0.2.123 → 1.0.2.125) touches only
`docs/acceptance/issue-26-production.md`, so the plugin source is identical to
**1.0.2.123** (PR #56, issue #26, eager pre-grab unmonitoring). 1.0.2.125 exists only
because every push to main increments the build number.

## Deployed artifact

The only recorded hash is the one in PR #59's backup bundle
(`/var/tmp/uniquesingles-deploy-5OEWRNHf/`, root only): the 1.0.2.125 DLL as it was
installed immediately before the 1.0.2.128 swap,
`plugin-before-1.0.2.125.dll` SHA-256
`2c9426dc45725ce9fc826f43b7cfe12c95c3f3b271c321bea0b9d585aa5478ec`, and
`plugin-before-1.0.2.125.pdb` SHA-256
`6b3515c044340ec98b935d81d2266bae1186f94316c2908133a76af8e389f569`. Byte comparison of
these hashes against the 1.0.2.125 release ZIP members is **unverifiable** from the
repository alone (the ZIP digest above covers the archive, not its members).

## Not recorded

The following are **unverifiable** for this deployment: the deployment date/time and
method, container restart time, Lidarr version and runtime at the time (123's receipt
shows bundled Lidarr 3.2.0.5175; 128's receipt shows 4.0.0.5227 on .NET 10.0.15, so the
image changed at some point between), its own backup bundle and rollback hashes,
pre/post settings snapshots (128's receipt shows all eight fields identical before and
after its own swap), and post-deploy API/log verification. 1.0.2.125's scan on
2026-11-21 is the only recorded evidence that it ran.

## Rollback

No rollback record exists for this deployment. Superseded by 1.0.2.128
([pr-59-production.md](pr-59-production.md)), whose bundle holds the 125 DLL/PDB.
