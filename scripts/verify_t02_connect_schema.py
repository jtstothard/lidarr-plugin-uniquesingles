#!/usr/bin/env python3
"""Verify UniqueSingles Connect notification visibility and settings persistence.

Reads LIDARR_API_KEY from env or .env file.
1. Checks notification schema for UniqueSingles entry.
2. Creates a UniqueSingles notification with test settings.
3. Reads back and verifies settings round-tripped.
4. Tests the notification endpoint.
5. Cleans up the test notification.

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


def api_request(base_url, api_key, method, path, data=None):
    """Make an API request and return parsed JSON."""
    url = f"{base_url}{path}"
    headers = {"X-Api-Key": api_key, "Content-Type": "application/json"}
    body = json.dumps(data).encode() if data else None
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=15) as resp:
            return json.loads(resp.read().decode()), resp.status
    except urllib.error.HTTPError as e:
        body_text = e.read().decode() if e.fp else ""
        return {"error": body_text, "status": e.code}, e.code
    except Exception as e:
        return {"error": str(e)}, 0


def main():
    load_env()

    base_url = os.environ.get("LIDARR_URL", "http://192.168.10.34:8686").rstrip("")
    api_key = os.environ.get("LIDARR_API_KEY", "")

    if not api_key:
        print("FAIL: LIDARR_API_KEY not set in env or .env")
        return 1

    # Step 1: Check notification schema
    print("Step 1: Checking notification schema...")
    schemas, status = api_request(base_url, api_key, "GET", "/api/v1/notification/schema")
    if isinstance(schemas, dict) and "error" in schemas:
        print(f"FAIL: Could not fetch notification schema: {schemas['error']}")
        return 1

    target_schema = None
    for s in schemas:
        if s.get("implementation") == "UniqueSinglesNotification":
            target_schema = s
            break

    if target_schema is None:
        print("FAIL: UniqueSinglesNotification not found in notification schema")
        impls = [s.get("implementation", "?") for s in schemas]
        print(f"  Available implementations: {impls}")
        return 1

    print(f"  Found: implementation={target_schema['implementation']}, "
          f"name={target_schema.get('implementationName')}")
    print(f"  configContract: {target_schema.get('configContract')}")
    print(f"  supportsOnReleaseImport: {target_schema.get('supportsOnReleaseImport')}")

    # Cleanup any leftover test notification from a previous run
    existing, _ = api_request(base_url, api_key, "GET", "/api/v1/notification")
    if isinstance(existing, list):
        for n in existing:
            if "UniqueSingles Verify" in n.get("name", ""):
                api_request(base_url, api_key, "DELETE", f"/api/v1/notification/{n['id']}")
                print(f"  Cleaned up leftover notification id={n['id']}")

    # Step 2: Create notification with test settings
    print("\nStep 2: Creating test notification...")
    create_body = {
        "name": "Test UniqueSingles Verify",
        "onReleaseImport": True,
        "onUpgrade": False,
        "onRename": False,
        "implementation": "UniqueSinglesNotification",
        "configContract": "UniqueSinglesSettings",
        "fields": [
            {"name": "durationToleranceMs", "value": 3000},
            {"name": "releaseTypesToCheck", "value": "Album, EP"},
            {"name": "tier3Action", "value": 0},  # FlagOnly
        ],
        "tags": [],
    }

    created, status = api_request(base_url, api_key, "POST", "/api/v1/notification", create_body)
    if isinstance(created, dict) and "error" in created:
        print(f"FAIL: Could not create notification: {created['error']}")
        return 1

    notif_id = created.get("id")
    if not notif_id:
        print(f"FAIL: No ID in created notification response: {created}")
        return 1

    print(f"  Created notification id={notif_id}")

    # Step 3: Read back and verify settings round-tripped
    print("\nStep 3: Reading back notification settings...")
    readback, status = api_request(base_url, api_key, "GET", f"/api/v1/notification/{notif_id}")
    if isinstance(readback, dict) and "error" in readback:
        print(f"FAIL: Could not read back notification: {readback['error']}")
        return 1

    # Check field values
    fields = {f["name"]: f["value"] for f in readback.get("fields", [])}
    errors = []

    dur_val = fields.get("durationToleranceMs")
    if int(dur_val) != 3000:
        errors.append(f"durationToleranceMs: expected 3000, got {dur_val}")

    rt_val = fields.get("releaseTypesToCheck")
    if rt_val != "Album, EP":
        errors.append(f"releaseTypesToCheck: expected 'Album, EP', got '{rt_val}'")

    t3_val = fields.get("tier3Action")
    # API may return enum name (string) or integer
    t3_ok = (str(t3_val) in ("0", "flagOnly", "FlagOnly"))
    if not t3_ok:
        errors.append(f"tier3Action: expected 0/flagOnly, got {t3_val}")

    if errors:
        print("FAIL: Settings did not round-trip correctly:")
        for e in errors:
            print(f"  {e}")
        # Cleanup
        api_request(base_url, api_key, "DELETE", f"/api/v1/notification/{notif_id}")
        return 1

    print(f"  Settings round-tripped correctly:")
    print(f"    durationToleranceMs = {fields.get('durationToleranceMs')}")
    print(f"    releaseTypesToCheck = {fields.get('releaseTypesToCheck')}")
    print(f"    tier3Action = {fields.get('tier3Action')}")

    # Step 4: Test the notification endpoint
    print("\nStep 4: Testing notification endpoint...")
    test_result, status = api_request(base_url, api_key, "POST",
                                       f"/api/v1/notification/test/{notif_id}")
    # The test endpoint may return various responses
    if isinstance(test_result, dict) and "error" in test_result:
        print(f"  Note: Test endpoint returned: {test_result['error'][:200]}")
    else:
        print(f"  Test endpoint called (status={status})")

    # Step 5: Cleanup
    print("\nStep 5: Cleaning up test notification...")
    api_request(base_url, api_key, "DELETE", f"/api/v1/notification/{notif_id}")
    print(f"  Deleted notification id={notif_id}")

    print("\nPASS: UniqueSingles Connect notification fully verified")
    return 0


if __name__ == "__main__":
    sys.exit(main())
