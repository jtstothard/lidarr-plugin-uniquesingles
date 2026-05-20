#!/usr/bin/env python3
"""Verify UniqueSingles plugin discovery in Lidarr.

Reads LIDARR_API_KEY from env or .env file.
Calls GET /api/v1/system/plugins and asserts UniqueSingles appears
with correct name, owner, and githubUrl.

Exits 0 on success, 1 on failure.
Uses urllib.request only — no subprocess pipes.
"""

import json
import os
import sys
import urllib.request
from pathlib import Path


def load_env():
    """Load .env file if present."""
    env_path = Path(__file__).resolve().parent.parent / ".env"
    if env_path.exists():
        for line in env_path.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if "=" in line:
                key, _, value = line.partition("=")
                os.environ.setdefault(key.strip(), value.strip())


def main():
    load_env()

    base_url = os.environ.get("LIDARR_URL", "http://192.168.10.34:8686").rstrip("/")
    api_key = os.environ.get("LIDARR_API_KEY", "")

    if not api_key:
        print("FAIL: LIDARR_API_KEY not set in env or .env")
        return 1

    url = f"{base_url}/api/v1/system/plugins"
    req = urllib.request.Request(url, headers={"X-Api-Key": api_key})

    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            plugins = json.loads(resp.read().decode())
    except Exception as e:
        print(f"FAIL: Could not fetch plugins: {e}")
        return 1

    # Find UniqueSingles
    target = None
    for p in plugins:
        if p.get("name") == "Unique Singles":
            target = p
            break

    if target is None:
        print("FAIL: 'Unique Singles' not found in installed plugins")
        print(f"  Available: {[p.get('name') for p in plugins]}")
        return 1

    # Verify metadata
    errors = []
    if target.get("owner") != "jtstothard":
        errors.append(f"owner: expected 'jtstothard', got '{target.get('owner')}'")
    if target.get("githubUrl") != "https://github.com/jtstothard/lidarr-plugin-uniquesingles":
        errors.append(f"githubUrl: expected 'https://github.com/jtstothard/lidarr-plugin-uniquesingles', got '{target.get('githubUrl')}'")

    if errors:
        print("FAIL: Metadata mismatch:")
        for e in errors:
            print(f"  {e}")
        return 1

    print(f"PASS: Unique Singles plugin discovered")
    print(f"  name: {target['name']}")
    print(f"  owner: {target['owner']}")
    print(f"  githubUrl: {target['githubUrl']}")
    print(f"  installedVersion: {target.get('installedVersion')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
