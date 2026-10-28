# Contributing

Thanks for considering a contribution to Unique Singles.

## Getting Started

Builds reference the official Lidarr 3.1.3.4968 binaries. Lidarr source submodules
are retained for reference, but are not required for building or testing.
Build hosts need Python 3.11+ and the .NET 8 SDK (CI uses 8.0.405). The preparation
script supports Linux and macOS on x64 and arm64.

```bash
git clone https://github.com/jtstothard/lidarr-plugin-uniquesingles.git
cd lidarr-plugin-uniquesingles
python3 scripts/prepare-lidarr.py
```

### Build

The preparation script downloads a platform-specific archive, checks its pinned
SHA-256 digest, and copies only top-level managed DLLs into the ignored
`_temp/lidarr/3.1.3.4968` directory. Subsequent runs reverify the cached archive
and replace the extracted references. No Lidarr process is installed or started.

```bash
dotnet restore UniqueSingles.sln
dotnet build UniqueSingles.sln -c Release --no-restore
```

### Test

```bash
python3 -m unittest discover -s tests -p 'test_prepare_lidarr.py'
dotnet test tests/UniqueSingles.Test/UniqueSingles.Test.csproj -c Release --no-build
```

The plugin uses non-copying binary references; tests copy their Lidarr runtime
dependencies into the test output and use the ASP.NET Core shared framework.
The release ZIP contains only the plugin DLL and PDB, as before. Builds do not
compile ImageSharp or run its license-key build target. This does not update
Lidarr or its runtime dependencies on users' systems.

When changing the supported Lidarr release, update the version/digests in
`scripts/prepare-lidarr.py`, the path in `LidarrReferences.targets`, and the
assembly-version guard in `AssemblySmokeTests.cs`. Verify the required plugin
APIs and the full suite against the actual downloaded binaries. NuGet audit
checks cover restored packages; they do not inventory the binaries in the
downloaded Lidarr archive.

## Code Style

The project uses StyleCop analyzers (see `stylecop.json`). Match the existing formatting and conventions in the file you're editing.

## Making Changes

1. Fork the repo and create a branch from `main`.
2. Make your change, with tests where practical — the matching-tier logic in particular relies on test coverage to stay safe (this plugin deletes files, so regressions are high-stakes).
3. Make sure `dotnet build` and `dotnet test` pass locally.
4. Open a pull request against `main`. CI must pass before merge.
5. Update `CHANGELOG.md` under an `[Unreleased]` heading if your change is user-facing.

## Reporting Bugs

Use the [bug report template](.github/ISSUE_TEMPLATE/bug_report.yml) when filing an issue. Include your Lidarr version, plugin version, and relevant logs — matching/deletion bugs are much easier to diagnose with a log excerpt.

For security-sensitive issues (e.g. unsafe deletion behavior), see [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Disposable exact-audio acceptance

The audio tests require an operator-provided FFmpeg (qualified: 9.0.2); they do
not skip when it is missing. Release builds generate the DLL/PDB-only ZIP checked
by `PluginIdentitySmokeTests`; CI publishes that same package. After the full
Release/Python gates, run `python3 scripts/qualify-disposable-host.py` with Python
3.12+ for the fresh-database, synthetic-library native command/import qualification.
See [operations](docs/exact-flac-analysis.md) and [acceptance evidence](docs/acceptance/issue-42.md).

CI explicitly installs the Ubuntu FFmpeg package before running the audio tests
and records its package/build version in the job log. This is a test dependency;
the plugin archive does not bundle a decoder. Do not rely on hosted runners having
FFmpeg preinstalled.
