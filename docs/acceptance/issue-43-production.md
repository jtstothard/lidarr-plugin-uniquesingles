# PR #43 production deployment — 2026-10-28

Authorized deployment and explicit cleanup activation completed on Proxmox VM102
(`media-vm`), container `lidarr`. No immediate production scan was requested or run.
The existing import handler and daily schedule now have exact-audio cleanup authority.

## Trusted source and runtime

- Merged main: `f7eaee6516e243639a022f506da655d71701f429`.
- Main push [CI run 40719123777](https://github.com/jtstothard/lidarr-plugin-uniquesingles/actions/runs/40719123777),
  run number 98, Build Plugin passed. Downloaded its draft release `1.0.2.98` through
  authenticated GitHub CLI; no local `_temp` build was trusted.
- Release ZIP SHA-256: `37870a5d420e7047e133af1d7052ff7f66ca290dc2b6a16827ba11e1fb8dd540`.
- Deployed/qualified DLL SHA-256: `575c89be0cd881f3c75f2a0fbf2efbbf7794bbabc5b49030786c7dcfeb11b46d`.
- PDB SHA-256: `9f226cba3bf0920cd3d05730150a1d4391c8b0137db6477e8b570bc539128a53`.
- Unchanged production image: `lscr.io/linuxserver/lidarr:nightly`, image/repository
  digest `sha256:e3f35175a0c68db1ea7e438c65af33ad3b4dece87c914cbbb35751cc68c23479`.
  Alpine 3.24.3/musl x86_64; bundled Lidarr **3.2.0.5150**, account **99:100** (`abc`).
- Existing plugin path: `/config/plugins/jtstothard/lidarr-plugin-uniquesingles/`.
  Host config bind: `/srv/docker-state/stacks/media/lidarr`.
  Other plugins, container mounts, image version and unrelated services were preserved.

## Decoder and isolated qualification

No FFmpeg existed in production. The durable decoder tree is now
`/config/exact-audio-decoder`, retained by the existing persistent config mount.
Alpine's signed repositories supplied **ffmpeg 8.1.3-r0**, with its complete dependency
closure in a separate `apk --root` tree (114 packages, approximately 135 MiB).
No production image/package installation, compose change or transient apt installation
was used. The root-owned tree is readable/executable by UID 99; normal files/directories
are not writable by that account.

The absolute entry point `/config/exact-audio-decoder/bin/ffmpeg` is a small shell
wrapper setting `LD_LIBRARY_PATH` to this tree's `usr/lib`, `lib`, and
`usr/lib/pulseaudio`, then exec'ing its `usr/bin/ffmpeg`. The wrapper and every regular
file/symlink in the deployed tree were compared with the qualified tree after restart.

- FFmpeg executable SHA-256: `c5b305d611f0c586a71db13734db6ef7a84747ffca88a0ab95edbfc954bdee2b`.
- Wrapper SHA-256: `1232bd4c63bf77caed6bedf80ec4da966573ba864532922344dff9568ca9390b`.
- Complete retained decoder archive SHA-256: `dc5fa1d5acb03244fb630a34f5f304abc99cbbe6cd5d9230b025cf2f82a5b813`.
- Package build commit: `66cae15cc19799ecaefa1ba9b131aeba0e5f7a86` in Alpine aports,
  origin `ffmpeg`. Package metadata, full dependency database/file hashes, FFmpeg
  configuration and its own `-L` license notice are retained. This GPL/version3-enabled
  local build is not shipped in the plugin or this repository; any later redistribution
  needs its applicable notices and corresponding source, including dependency/build
  obligations. The receipt pins the installed package tree rather than a moving URL.

The **same 14 native synthetic stages passed**, exit 0, completed=true, before enabling
production cleanup. See [native results](issue-43-production/native-results.json).
The isolated container used the exact production image and bundled host binaries,
UID/GID 99:100, the exact decoder mounted at its eventual absolute runtime path,
new database and synthetic FLAC only. Limits were 2 CPUs, 1536 MiB memory/swap,
256 PIDs and no-new-privileges. No production config, music, download mounts, Docker
socket, published ports or external providers were supplied. Network was disconnected
before the suite. Container root retained Docker's default capability set for harness
preparation; the suite and all Lidarr/decoder processes ran as unprivileged UID 99.

Python 3.14.10 and seven harness dependency packages were added only to that disposable
container; apk reported installations, no upgrades to existing image packages. The
[harness patch](issue-43-production/harness.patch) replaces pinned glibc archive extraction
with a copy of `/app/lidarr/bin` and explicitly records the image/merged source identity.
All fixtures, assertions and 14 scenario bodies are unchanged. Production received no
Python. The temporary container was stopped and removed; its marked evidence remains.

The suite proved report-only preservation, warm-cache behavior, changed-source rejection,
missing-decoder rejection, Connect precedence, self/unmonitored-source protection,
exclusive B-side preservation, real ManualImport report-only and opt-in behavior,
explicit scan cleanup, scheduled command/restart timing, and equivalent-single survivor
retention. Early harness attempts failed before native execution because of directory
permissions and a missing PulseAudio library search directory; final qualification used
the corrected, pinned wrapper. No production settings were changed by those attempts.

## Backup, activation and verification

Private persistent receipt directory on VM102 (root-only, mode 0700):
`/srv/docker-state/deployment-receipts/uniquesingles-20261028/`.
SSH used `proxmox` and `qm guest exec 102`; no direct guest SSH/authentication changes.

Before replacement, Lidarr was gracefully stopped and `rollback-config.tar` captured
`config.xml`, `lidarr.db` and any WAL/SHM, logs database, **all plugins**, and `asp` if
present, as one coherent stopped-state backup. Settings live in that backed-up database.
Existing old backup files, logs and MediaCover were not duplicated. Music was not part
of the backup. `rollback-image.tar` saves the exact original image; the private
`container-before.private.json` retains full original container configuration.

- Config backup SHA-256: `3d88b2dfaee7afb83996fdb74f82aaa1e9d1710d57f92c1d1f76953a5ee9a276`.
- Image archive SHA-256: `0a12bbcbe950bf31e07a8588e5bd98f2a5028801bada770574d2862e382f1b6b`.

Installed the tested DLL/PDB through the existing plugin directory, started Lidarr,
then updated its existing providers through its authenticated HTTP API. Credentials
were parsed locally from XML and never printed or checked in. Updated **Connect ID 54**
(first-priority owner, existing onReleaseImport enabled) and **Metadata ID 15**
(existing enabled fallback/schedule owner):

| Setting | Before | After |
| --- | --- | --- |
| exactAudioAnalysis | absent/default false | true |
| exactAudioCleanup | absent/default false | true |
| exactAudioDecoderPath | absent/default `ffmpeg` | `/config/exact-audio-decoder/bin/ffmpeg` |

Preserved duration tolerance **3000 ms**, source types **Album, EP**, Tier 3 **FlagOnly**,
provider events/tags/enabled state and scan interval **1440 minutes**. SQLite comparisons
against the stopped backup verified all Config, Metadata, Notifications, Indexers,
DownloadClients, ImportLists, RemotePathMappings, QualityProfiles, MetadataProfiles,
DelayProfiles and Tags rows; only the three intended settings fields differed.
No provider was recreated.

A second targeted graceful container restart proved persistence: plugin API reports
installed **1.0.2.98**, DLL hash matches, API responds, decoder runs as 99:100,
provider state persists and scheduler timestamps remain:

- Last execution: **2026-10-28T11:57:27Z**.
- Next execution: **2026-10-29T11:57:27Z**.
- Interval: **1440 minutes**. Internal task row ID changes during startup registration;
  execution times and interval are preserved.

Production has **not run a new cleanup scan during this deployment**; the last scan
remains the earlier 11:57 execution. Synthetic cleanup actually ran and passed in
isolation. Activation is verified by persisted effective ownership, loaded implementation
and decoder proof; no immediate full-library sweep was forced. Existing future imports
and the unchanged next scheduled scan can now perform eligible exact cleanup.

The container is running and the host API works. Health reports an error for the
qBittorrent `/data/downloads/torrents/complete` directory not existing in-container,
and a Prowlarr indexer unavailable for over six hours. These are outside this deployment;
no unrelated settings were changed to address them. A predeployment health API snapshot
was not taken, so this report does not claim a before/after health comparison.

Rollback was not needed. To revert, gracefully stop Lidarr, restore the saved DB/config/
plugins coherently from `rollback-config.tar` (remove current SQLite WAL/SHM before
restoring), keep/load the pinned saved image, and start the existing container. The old
settings omit both audio opt-ins. The unused decoder tree can remain until an explicitly
scoped removal. Full private receipts include provider snapshots, verification scripts,
container inspection, dependency manifest, decoder archive and synthetic logs/database;
only sanitized outcome evidence is checked in here.

Validation: successful main CI build/tests, 14/14 production-runtime synthetic native
stages, API/plugin/hash/account checks, stopped-backup settings comparison, restart
persistence and schedule preservation. No product source change or local C# rebuild was
needed. No push, additional PR, merge, watcher or unrelated service change was performed.
