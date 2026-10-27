"""Tests for the archive boundary used to prepare prebuilt references."""

import hashlib
import importlib.util
import io
import tarfile
import tempfile
import unittest
from pathlib import Path


spec = importlib.util.spec_from_file_location(
    "prepare_lidarr", Path(__file__).resolve().parents[1] / "scripts" / "prepare-lidarr.py"
)
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "lidarr.tar.gz"
        self.destination = self.root / "references"
        self.destination.mkdir()

    def write_archive(self, extras=(), omit=None):
        with tarfile.open(self.archive, "w:gz") as bundle:
            for name in sorted(prepare.REQUIRED - {omit}):
                member = tarfile.TarInfo(f"Lidarr/{name}")
                member.size = 3
                bundle.addfile(member, io.BytesIO(b"dll"))
            for member in extras:
                bundle.addfile(member, io.BytesIO(b""))

    def test_verified_archive_extracts_only_top_level_assemblies(self):
        self.write_archive([tarfile.TarInfo("Lidarr/../../escaped.dll")])
        expected = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        prepare.verify_archive(self.archive, expected)
        self.assertEqual(len(prepare.REQUIRED), prepare.extract_assemblies(self.archive, self.destination))
        self.assertEqual(prepare.REQUIRED, {p.name for p in self.destination.iterdir()})
        self.assertFalse((self.root / "escaped.dll").exists())

    def test_modified_archive_fails_checksum_verification(self):
        self.write_archive()
        expected = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        with self.archive.open("ab") as stream:
            stream.write(b"tampered")
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            prepare.verify_archive(self.archive, expected)

    def test_symlink_cannot_become_an_assembly(self):
        link = tarfile.TarInfo("Lidarr/NLog.dll")
        link.type = tarfile.SYMTYPE
        link.linkname = "/outside/NLog.dll"
        self.write_archive([link], omit="NLog.dll")
        with self.assertRaisesRegex(ValueError, "Invalid or duplicate"):
            prepare.extract_assemblies(self.archive, self.destination)

    def test_duplicate_assembly_is_rejected(self):
        self.write_archive([tarfile.TarInfo("Lidarr/NLog.dll")])
        with self.assertRaisesRegex(ValueError, "Invalid or duplicate"):
            prepare.extract_assemblies(self.archive, self.destination)

    def test_incomplete_release_is_rejected(self):
        self.write_archive(omit="Lidarr.Core.dll")
        with self.assertRaisesRegex(ValueError, "Required Lidarr assemblies missing"):
            prepare.extract_assemblies(self.archive, self.destination)


if __name__ == "__main__":
    unittest.main()
