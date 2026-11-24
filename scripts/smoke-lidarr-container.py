#!/usr/bin/env python3
"""Load the built plugin into a real, throwaway Lidarr container and exercise it.

Unit tests cover the plugin against reference assemblies; this checks that a real
Lidarr host (the LinuxServer image production runs) can load the plugin, resolve its
dependency graph, create its Connect and Metadata entries, and run a scan. The
container has an empty /config, no network and no media, so the scan touches nothing.

Usage: smoke-lidarr-container.py <plugin.zip | dir with DLL+PDB> <image> [expected-version]
Exit code 0 only when every check passes. Requires docker.
"""
import json
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import zipfile
from pathlib import Path

PLUGIN_FILES = ("Lidarr.Plugin.UniqueSingles.dll", "Lidarr.Plugin.UniqueSingles.pdb")
PLUGIN_SUBDIR = "plugins/jtstothard/lidarr-plugin-uniquesingles"
NOTIFICATION = "UniqueSinglesNotification"
METADATA = "UniqueSinglesScanTask"
SCAN_COMMAND = "UniqueSinglesScan"
# Log lines that indicate the plugin (or its DI graph) failed, whatever the level.
PLUGIN_FAILURE = re.compile(
    r"UniqueSingles|Unique Singles|DryIoc|Unable to resolve|Failed to load plugin", re.I
)
LEVEL = re.compile(r"\|(Warn|Error|Fatal)\|")
ENTRY_START = re.compile(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}")
# Expected with --network none: the host's own online lookups fail.
NETWORK_NOISE = re.compile(
    r"ServerSideNotificationService|Failed to get remote plugin|No compatible release found|"
    r"SystemTimeCheck|XmlKeyManager|Name or service not known|Network is unreachable",
    re.I,
)


def run(*args, check=True, capture=True):
    return subprocess.run(args, check=check, text=True,
                          stdout=subprocess.PIPE if capture else None,
                          stderr=subprocess.STDOUT if capture else None)


class Smoke:
    def __init__(self, plugin, image, expected_version):
        self.plugin = Path(plugin)
        self.image = image
        self.expected_version = expected_version
        self.name = "us-smoke-" + uuid.uuid4().hex[:8]
        self.work = Path(tempfile.mkdtemp(prefix="us-smoke-"))
        self.config = self.work / "config"
        self.key = None
        self.failures = []

    # -- helpers -------------------------------------------------------------
    def check(self, ok, what):
        print(("PASS " if ok else "FAIL ") + what, flush=True)
        if not ok:
            self.failures.append(what)
        return ok

    def api(self, method, path, body=None):
        cmd = ["docker", "exec", self.name, "curl", "-sS", "-o", "-", "-w", "\n%{http_code}",
               "-X", method, "-H", f"X-Api-Key: {self.key}", "-H", "Content-Type: application/json",
               f"http://localhost:8686/api/v1/{path}"]
        if body is not None:
            cmd += ["--data-binary", json.dumps(body)]
        out = run(*cmd, check=False).stdout
        text, _, code = out.rpartition("\n")
        try:
            return int(code), (json.loads(text) if text.strip() else None)
        except ValueError:
            return int(code) if code.isdigit() else 0, text

    def log_lines(self):
        """Log entries, with exception/stack continuation lines folded into their entry."""
        logs = sorted((self.config / "logs").glob("lidarr*.txt"))
        entries = []
        for f in logs:
            for line in f.read_text(errors="replace").splitlines():
                if ENTRY_START.match(line) or not entries:
                    entries.append(line)
                else:
                    entries[-1] += "\n" + line
        return entries

    # -- steps ---------------------------------------------------------------
    def install_plugin(self):
        dest = self.config / PLUGIN_SUBDIR
        dest.mkdir(parents=True)
        if self.plugin.is_file():
            with zipfile.ZipFile(self.plugin) as z:
                names = set(z.namelist())
                missing = [f for f in PLUGIN_FILES if f not in names]
                if missing:
                    raise SystemExit(f"plugin zip lacks {missing}: {sorted(names)}")
                for f in PLUGIN_FILES:
                    (dest / f).write_bytes(z.read(f))
        else:
            for f in PLUGIN_FILES:
                shutil.copy2(self.plugin / f, dest / f)
        # The image chowns /config to its own user (PUID, default 911) on start.
        run("chmod", "-R", "a+rwX", str(self.work))

    def start(self):
        run("docker", "run", "-d", "--name", self.name, "--network", "none", "-e", "TZ=Etc/UTC",
            "-v", f"{self.config}:/config", self.image)
        deadline = time.time() + 300
        cfg = self.config / "config.xml"
        while time.time() < deadline:
            logs = run("docker", "logs", self.name, check=False).stdout
            if "EPIC FAIL" in logs:  # host crashed during startup; it would only loop
                print("Lidarr crashed during startup", flush=True)
                return False
            if cfg.exists():
                m = re.search(r"<ApiKey>([^<]+)</ApiKey>", cfg.read_text(errors="replace"))
                if m:
                    self.key = m.group(1)
                    code, status = self.api("GET", "system/status")
                    if code == 200:
                        print(f"Lidarr {status.get('version')} on .NET {status.get('runtimeVersion')} "
                              f"(branch {status.get('branch')})", flush=True)
                        return True
            time.sleep(2)
        return False

    def run_checks(self):
        code, plugins = self.api("GET", "system/plugins")
        ours = [p for p in (plugins or []) if isinstance(p, dict) and p.get("name") == "Unique Singles"]
        self.check(code == 200 and len(ours) == 1, "host lists the Unique Singles plugin")
        if ours and self.expected_version:
            self.check(ours[0].get("installedVersion") == self.expected_version,
                       f"installed version {ours[0].get('installedVersion')} == {self.expected_version}")

        definitions = {}
        for kind, impl, extra in (("notification", NOTIFICATION, {"onReleaseImport": True}),
                                  ("metadata", METADATA, {"enable": True})):
            code, schema = self.api("GET", f"{kind}/schema")
            match = [s for s in (schema or []) if isinstance(s, dict) and s.get("implementation") == impl]
            if not self.check(code == 200 and len(match) == 1, f"{kind} schema offers {impl}"):
                continue
            body = dict(match[0], name=f"smoke {impl}", **extra)
            code, created = self.api("POST", kind, body)
            ok = code in (200, 201) and isinstance(created, dict) and created.get("id")
            self.check(bool(ok), f"create {kind} {impl} (HTTP {code})")
            if ok:
                definitions[kind] = created["id"]

        code, cmd = self.api("POST", "command", {"name": SCAN_COMMAND})
        if self.check(code in (200, 201) and isinstance(cmd, dict), f"queue {SCAN_COMMAND} (HTTP {code})"):
            status, message = None, None
            deadline = time.time() + 120
            while time.time() < deadline:
                _, c = self.api("GET", f"command/{cmd['id']}")
                status, message = (c or {}).get("status"), (c or {}).get("message")
                if status in ("completed", "failed", "aborted", "cancelled", "orphaned"):
                    break
                time.sleep(2)
            self.check(status == "completed", f"scan command {status}: {message}")

        time.sleep(2)  # let the logger flush
        lines = self.log_lines()
        self.check(any("Registered scheduled task" in l and "UniqueSinglesScanCommand" in l for l in lines),
                   "scheduler registered the Unique Singles scan")
        self.check(any("UniqueSingles scan complete:" in l for l in lines), "scan logged its completion")
        bad = [l for l in lines if LEVEL.search(l) and not NETWORK_NOISE.search(l)
               and (PLUGIN_FAILURE.search(l) or "|Fatal|" in l)]
        self.check(not bad, "no plugin-related warning/error and no fatal line in the host log")
        for l in bad[:10]:
            print("  " + l[:3000])
        other = [l for l in lines if LEVEL.search(l) and l not in bad and not NETWORK_NOISE.search(l)]
        if other:
            print("Other host warnings/errors (not failing):")
            for l in other[-20:]:
                print("  " + l.split("\n")[0])

    def cleanup(self):
        if self.failures:
            print("---- container log tail ----")
            print(run("docker", "logs", "--tail", "60", self.name, check=False).stdout)
        run("docker", "rm", "-f", self.name, check=False)
        # Files written by the container may belong to its user; remove via the image.
        run("docker", "run", "--rm", "--entrypoint", "sh", "-v", f"{self.work}:/w", self.image,
            "-c", "rm -rf /w/config", check=False)
        shutil.rmtree(self.work, ignore_errors=True)

    def main(self):
        try:
            self.install_plugin()
            if not self.check(self.start(), f"Lidarr ({self.image}) API came up"):
                return 1
            self.run_checks()
        finally:
            self.cleanup()
        print(f"{'FAILED' if self.failures else 'OK'}: {len(self.failures)} failing check(s)")
        return 1 if self.failures else 0


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        raise SystemExit(__doc__)
    sys.exit(Smoke(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) == 4 else None).main())
