# Contributing

Thanks for considering a contribution to Unique Singles.

## Getting Started

This repo uses a git submodule pointing at Lidarr's source (`ext/Lidarr`), which the plugin builds against.

```bash
git clone --recurse-submodules https://github.com/jtstothard/lidarr-plugin-uniquesingles.git
cd lidarr-plugin-uniquesingles
# If you already cloned without --recurse-submodules:
git submodule update --init --recursive
```

### Build

```bash
dotnet restore UniqueSingles.sln
dotnet build UniqueSingles.sln -c Release
```

### Test

```bash
dotnet test tests/UniqueSingles.Test/UniqueSingles.Test.csproj -c Release
```

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
