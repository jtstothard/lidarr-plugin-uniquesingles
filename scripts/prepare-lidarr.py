#!/usr/bin/env python3
"""Prepare verified, prebuilt Lidarr references without installing or running Lidarr."""

import hashlib
import platform
import shutil
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path, PurePosixPath


VERSION = "3.1.3.4968"
# GitHub release asset digests, not checksums fetched alongside the download.
# https://github.com/Lidarr/Lidarr/releases/tag/v3.1.3.4968
SHA256 = {
    "linux-core-x64": "8e14e5a2e9a89f7cb7a281434a7484d1b9632fc52f2cc06bb03a5102714dddd1",
    "linux-core-arm64": "e5c89688850885bc7becd779e668bbf423ca3fe37986704b70d9e9b217ea9e44",
    "osx-core-x64": "3143137d84cf8a06b4efda544af88c360247256993d05af05bb7a725ef3e96ed",
    "osx-core-arm64": "7fbebd4e5a5b01a8489a5dc733d7bb40c6881a42ccc87d32ace72640257bb725",
}
REQUIRED = {
    "Lidarr.Core.dll", "Lidarr.Common.dll", "Lidarr.Http.dll", "Lidarr.Api.V1.dll",
    "NLog.dll", "FluentValidation.dll", "Newtonsoft.Json.dll", "System.IO.Abstractions.dll",
}


def verify_archive(archive, expected):
    with archive.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != expected:
        raise ValueError(f"SHA-256 mismatch for {archive.name}: expected {expected}, got {actual}")


def extract_assemblies(archive, destination):
    """Copy only regular DLL files immediately inside Lidarr/, never archive paths."""
    found = set()
    with tarfile.open(archive, "r:gz") as bundle:
        for member in bundle:
            path = PurePosixPath(member.name)
            if len(path.parts) != 2 or path.parts[0] != "Lidarr" or path.suffix != ".dll":
                continue
            if not member.isfile() or path.name in found:
                raise ValueError(f"Invalid or duplicate assembly member: {member.name}")
            if member.size > 100 * 1024 * 1024:
                raise ValueError(f"Oversized assembly: {member.name}")
            with bundle.extractfile(member) as source, (destination / path.name).open("wb") as target:
                shutil.copyfileobj(source, target)
            found.add(path.name)
    missing = REQUIRED - found
    if missing:
        raise ValueError(f"Required Lidarr assemblies missing: {', '.join(sorted(missing))}")
    return len(found)


def main():
    system = {"Darwin": "osx", "Linux": "linux"}.get(platform.system())
    machine = {"arm64": "arm64", "aarch64": "arm64", "x86_64": "x64", "AMD64": "x64"}.get(platform.machine())
    rid = f"{system}-core-{machine}"
    if rid not in SHA256:
        raise ValueError("Supported build hosts: Linux or macOS, x64 or arm64")

    root = Path(__file__).resolve().parent.parent
    cache = root / "_temp" / "lidarr"
    cache.mkdir(parents=True, exist_ok=True)
    name = f"Lidarr.develop.{VERSION}.{rid}.tar.gz"
    archive = cache / name
    if not archive.exists():
        url = f"https://github.com/Lidarr/Lidarr/releases/download/v{VERSION}/{name}"
        print(f"Downloading {url}", flush=True)
        with tempfile.TemporaryDirectory(dir=cache) as temporary:
            download = Path(temporary) / name
            request = urllib.request.Request(url, headers={"User-Agent": "UniqueSingles-build"})
            with urllib.request.urlopen(request, timeout=60) as response, download.open("wb") as target:
                total = 0
                while chunk := response.read(1024 * 1024):
                    total += len(chunk)
                    if total > 256 * 1024 * 1024:
                        raise ValueError("Lidarr archive exceeds 256 MiB")
                    target.write(chunk)
            verify_archive(download, SHA256[rid])
            download.replace(archive)

    # Reverify cached archives and re-extract so edited DLLs cannot become references.
    verify_archive(archive, SHA256[rid])
    destination = cache / VERSION
    if destination.is_symlink():
        raise ValueError("Refusing to replace a symlink at the generated reference directory")
    with tempfile.TemporaryDirectory(dir=cache) as temporary:
        staged = Path(temporary) / VERSION
        staged.mkdir()
        count = extract_assemblies(archive, staged)
        if destination.exists():
            shutil.rmtree(destination)
        staged.replace(destination)
    print(f"Prepared {count} verified Lidarr {VERSION} assemblies in {destination}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, tarfile.TarError) as error:
        sys.exit(f"Lidarr preparation failed: {error}")
