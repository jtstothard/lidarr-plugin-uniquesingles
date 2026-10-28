# Spec #37: isolated Linux qualification on media-vm VM102

On 2026-10-28, all **14 synthetic native stages passed** on the existing Proxmox
server, using a separate temporary container and fresh Lidarr database. This is
Linux x86_64 qualification of the exact artifact and decoder identified below.
Production Lidarr, its settings, mounts, music and services were not accessed or changed.

## Identity and isolation

- Tested runner/fixtures commit: `ba75e0dd483f3fed9bb58d0340fb118f613de87b`
  (`t3/implement-issue-37`). Copied runner and fixtures without changes; no runner
  portability fix or product source change was necessary.
- Proxmox SSH alias `proxmox`, address `192.168.10.51`, hostname `proxmox1`,
  `pve-manager/9.2.2/b9984c6d90a4bd80`, kernel `7.0.2-6-pve`.
  Commands and file transfer used `ssh -o BatchMode=yes proxmox` with
  `qm guest exec 102 -- ...`; no guest SSH or authentication changes.
- VM **102**, `media-vm`, Debian **13.7**, x86_64,
  kernel `6.12.101+deb13-amd64`; Docker **29.6.1**, build `8900f1d`.
- Existing cached image (no pull/build/install):
  `ghcr.io/neptunehub/audiomuse-ai@sha256:811f1f0bd0de97a1b4ab6601898297010da18fc957312b1ed3bb62990683630c`.
  Image ID equals that digest. Container userspace reports Ubuntu **24.04.5 LTS**,
  glibc **2.39-0ubuntu8.9**, Python **3.12.3-0ubuntu2.1**.
- Test container `uniquesingles-linux-20261028`, ID
  `5f9c3f4e7d47908bca2a1955551b99d61c755b049c49e0ea8f06219696502a1b`.
  Overrode its entrypoint with `/bin/sh`; ran only the qualification runner.
  Limits: **2 CPUs, 1536 MiB RAM and swap total, 256 PIDs**, all capabilities
  dropped, `no-new-privileges`, network **none**, no published ports, no Docker
  socket, no production mounts. Its only mount was
  `/tmp/uniquesingles-linux-20261028` to `/qualification`.
- Native host **Lidarr 3.1.3.4968**, bundled .NET **8.0.27**, downloaded as
  `Lidarr.develop.3.1.3.4968.linux-core-x64.tar.gz` into that marked directory.
  Verified pinned archive SHA-256:
  `8e14e5a2e9a89f7cb7a281434a7484d1b9632fc52f2cc06bb03a5102714dddd1`.
  Download/preparation ran on the guest before the network-disabled container.
- Tested DLL SHA-256:
  `997da5b0bf9363024fefb3122b23b7e6c9503d8f8aad68f72c1d378cebd65b76`.
  Copied from the clean parent checkout's
  `_temp/bin/Release/UniqueSingles/Lidarr.Plugin.UniqueSingles.dll`.
  **Artifact boundary:** that file did not match the prior macOS receipt's
  `02bf8a861e334b2742106d0433ea0d2713f8302cd9f1f0e8e8d1464fc943282d`.
  The recorded older binary was not found in surviving worktrees. This run
  qualifies the available DLL bytes above, not the missing older binary; no
  rebuild was performed and no byte-equivalence claim is made.
- Decoder `/usr/bin/ffmpeg`, Ubuntu package **7:6.1.1-3ubuntu5**, FFmpeg **6.1.1**,
  SHA-256 `ed16af623947494a72e284b6eb8ff225f2da22b38b5d5069c2fd4b4ba3384e41`.
  Build enables GPL and shared libraries. The retained decoder receipt includes
  its full configuration, linked library paths, package versions and resolved
  shared-library hashes. The image's `/usr/share/doc/ffmpeg/copyright` was read
  and retained locally. No decoder binary was added to the plugin or redistributed.
  This is evidence for this installed build, not redistribution/license clearance
  for another image. Deployment must retain the exact package's applicable notices
  and source/license obligations described in [operations](../exact-flac-analysis.md).

## Observed results

Every command completed and every runner assertion passed:

1. Metadata fallback: tolerance 1234 ms, one report-only exact duplicate, preserved files.
2. Warm cache: same report-only result, preserved files.
3. Replaced source: one review, preserved files despite explicit cleanup enabled.
4. Absent decoder: one review, preserved files.
5. Connect precedence: tolerance 2345 ms and usable decoder override deliberately
   unusable Metadata decoder; report-only result overrides Metadata cleanup opt-in.
6. Self-source exclusion: candidate retained.
7. Unmonitored retained source: candidate retained.
8. Catalog-only exclusive B-side: whole release kept, zero release reviews.
9. Real ManualImport report-only: one file imported; asynchronous plugin handler
   reported one exact duplicate and retained imported candidate and source.
10. Explicit scan cleanup: exactly one candidate cleaned; retained source stayed
    monitored and present, candidate files disappeared.
11. Real ManualImport opt-in: imported candidate cleaned by actual notification
    handler, no candidate track files remain; retained source preserved.
12. Scheduled opt-in command: completed; interval 10080 minutes.
13. Restart scan: completed; scheduling preserved last execution
    `2026-10-28T14:22:57Z` and next execution `2026-11-04T14:22:57Z` unchanged.
    Disabling Metadata removed the task.
14. Two equivalent singles: exactly one cleaned, exactly one monitored/file survivor.

The [native receipt](issue-37-linux-host.json) retains all command responses,
host identity, plugin/archive/decoder hashes and only the plugin scheduling task.
[Plugin log excerpts](issue-37-linux-host.log.txt) retain actual import-handler and
cleanup decisions, with trailing whitespace normalized. [Environment/cleanup receipt](issue-37-linux-environment.json)
retains the exact container isolation and exit state.
[Decoder receipt](issue-37-linux-decoder.txt) records the native runtime dependencies.
These copied receipts were checked for API-key/password/token markers; no API key,
configuration XML or database was copied into the repository.

## Reproduction and cleanup

Copy the runner, preparation script, synthetic `AudioFixtures` and exact DLL into a
new marked guest `/tmp/uniquesingles-*` directory. Retain the source commit identity
for the runner's `git rev-parse HEAD` receipt. Run the archive preparation script
there, then launch the pinned image using the isolation settings above and execute
`python3 scripts/qualify-disposable-host.py` from the mounted repository layout.
No image entrypoint or production container command is used. Receipts remain in
`_temp/issue42-54736e26802747fba9cfd33fe61766e7` inside the marked directory.

The runner exited **0**, `completed=true`, 14 stages. Its `finally` stopped every
native Lidarr process. The test container exited normally, without OOM, and was
removed; its absence was checked afterward. Short-lived decoder/distro probe
containers used `--rm` and were removed automatically. The pre-existing running
container IDs/names/images were identical before and after. No production container
was exec'd, restarted, mounted or written. The marked guest directory remains as
local evidence (archive, fresh synthetic DB/library, logs and receipts); no host
package, service, authentication or production configuration changed.
Local copied full evidence: `_temp/linux-qualification/` in the qualification worktree.

Five Python preparation tests and `git diff --check` pass. No .NET rebuild or C#
test rerun was needed for this evidence-only change; the previous 251 C# result is
recorded separately in [issue #42 acceptance](issue-42.md), not claimed as rerun here.

## Limits

This qualifies this Ubuntu/glibc container, Linux pinned archive, available DLL and
FFmpeg 6.1.1 build on VM102. It does not qualify the production LinuxServer Lidarr
image, production permissions/accounts/mounts, the older unavailable DLL, Windows,
real-library behavior or a deployment. No settings activation, real cleanup,
push, PR creation or issue closure was performed. Native import proof is the
single-import route; other race/fault/cancellation/album-import cases retain the
cleanup-service test boundaries in the earlier acceptance notes. Container network
is disabled, so this run makes no external-provider/connectivity claim. Runtime
changes require requalification; executable hashes alone do not attest dependencies.
