"""Exercise the Windows PowerShell 5.1 release extractor on small ZIP fixtures."""
from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

PROJECT = Path(__file__).resolve().parents[2]
FIXTURES = PROJECT / ".build" / "release-extractor-tests"


class ExtractTests(unittest.TestCase):
    def setUp(self):
        FIXTURES.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="fixture-", dir=FIXTURES)
        self.root = Path(self.temporary.name).resolve()
        self.addCleanup(self.temporary.cleanup)
        shutil.copyfile(PROJECT / "scripts/release-tools/Extract-Zhidian.ps1", self.root / "Extract-Zhidian.ps1")

    def fixture(self, extra=None):
        entries = [("Zhidian.exe", b"safe synthetic executable fixture"), ("docs/README.txt", b"Hello release")]
        entries.extend(extra or [])
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, content in entries:
                archive.writestr(name, content)
        raw = stream.getvalue()
        archive_name = "Zhidian-test-win-x64.zip"
        parts = []
        for index, offset in enumerate(range(0, len(raw), 127), 1):
            block = raw[offset:offset + 127]
            name = f"{archive_name}.{index:03d}"
            (self.root / name).write_bytes(block)
            parts.append(dict(name=name, bytes=len(block), sha256=hashlib.sha256(block).hexdigest()))
        manifest = dict(version="test", directory="Zhidian-test-win-x64", archive=dict(
            name=archive_name, bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest(),
            unpackedBytes=sum(len(data) for _, data in entries), fileCount=len(entries)), parts=parts)
        self.write_manifest(manifest)
        return manifest, raw

    def write_manifest(self, manifest):
        (self.root / "release-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    def run_extractor(self, *arguments):
        result = subprocess.run(["powershell.exe", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                 str(self.root / "Extract-Zhidian.ps1"), *arguments],
                                capture_output=True, text=True, errors="replace", timeout=60)
        return result.returncode, result.stdout + result.stderr

    def assert_refused(self, expected):
        code, output = self.run_extractor()
        self.assertEqual(code, 1, output)
        self.assertIn(expected, output)
        self.assertFalse((self.root / "Zhidian-test-win-x64").exists(), output)
        return output

    def test_extract_all_parts_without_external_tools(self):
        manifest, raw = self.fixture()
        code, output = self.run_extractor()
        self.assertEqual(code, 0, output)
        self.assertEqual((self.root / manifest["directory"] / "docs/README.txt").read_bytes(), b"Hello release")
        self.assertFalse((self.root / manifest["archive"]["name"]).exists())
        self.assertFalse(list(self.root.glob(".zhidian-extract-*")))
        self.assertTrue(all((self.root / part["name"]).is_file() for part in manifest["parts"]))

    def test_custom_destination_and_keep_zip(self):
        manifest, raw = self.fixture()
        destination = self.root / "custom destination"
        code, output = self.run_extractor("-DestinationDirectory", str(destination), "-KeepArchive")
        self.assertEqual(code, 0, output)
        self.assertTrue((destination / "Zhidian.exe").is_file())
        self.assertEqual((self.root / manifest["archive"]["name"]).read_bytes(), raw)

    def test_long_engine_paths(self):
        name = "engines/gpt-sovits/runtime/Lib/site-packages/pkg_resources/tests/data/my-test-package_unpacked-egg/my_test_package-1.0-py3.7.egg/EGG-INFO/dependency_links.txt"
        manifest, _ = self.fixture([(name, b"engine fixture")])
        code, output = self.run_extractor()
        self.assertEqual(code, 0, output)
        self.assertEqual((self.root / manifest["directory"] / name).read_bytes(), b"engine fixture")

    def test_missing_part_is_refused(self):
        manifest, _ = self.fixture()
        (self.root / manifest["parts"][1]["name"]).unlink()
        self.assert_refused("Missing ZIP part")

    def test_same_size_corruption_is_refused(self):
        manifest, _ = self.fixture()
        part = self.root / manifest["parts"][0]["name"]
        raw = bytearray(part.read_bytes())
        raw[0] ^= 1
        part.write_bytes(raw)
        self.assert_refused("SHA-256 mismatch")

    def test_existing_user_directory_is_preserved(self):
        manifest, _ = self.fixture()
        destination = self.root / manifest["directory"]
        destination.mkdir()
        original = destination / "user-data.txt"
        original.write_bytes(b"must never change")
        code, output = self.run_extractor()
        self.assertEqual(code, 1, output)
        self.assertIn("destination already exists", output)
        self.assertEqual(original.read_bytes(), b"must never change")

    def test_parent_traversal_zip_is_refused_before_extraction(self):
        self.fixture([("../escaped.txt", b"bad")])
        self.assert_refused("unsafe Windows entry path")
        self.assertFalse((self.root / "escaped.txt").exists())

    def test_windows_device_zip_is_refused(self):
        self.fixture([("docs/CON.txt", b"bad")])
        self.assert_refused("unsafe Windows entry path")

    def test_case_collision_zip_is_refused(self):
        self.fixture([("zhidian.EXE", b"bad")])
        self.assert_refused("duplicate entry names")

    def test_file_directory_collision_zip_is_refused(self):
        self.fixture([("docs", b"bad")])
        self.assert_refused("conflicts with an extraction directory")

    def test_zip_symlink_is_refused(self):
        info = zipfile.ZipInfo("link")
        info.create_system = 3
        info.external_attr = 0o120777 << 16
        self.fixture([(info, b"../elsewhere")])
        self.assert_refused("symbolic link")

    def test_manifest_traversal_part_is_refused(self):
        manifest, _ = self.fixture()
        manifest["parts"][0]["name"] = "../outside.zip.001"
        self.write_manifest(manifest)
        self.assert_refused("unsafe file or directory name")

    def test_manifest_archive_hash_is_checked(self):
        manifest, _ = self.fixture()
        manifest["archive"]["sha256"] = "0" * 64
        self.write_manifest(manifest)
        self.assert_refused("Combined ZIP SHA-256 mismatch")

    def test_manifest_inventory_is_checked(self):
        manifest, _ = self.fixture()
        manifest["archive"]["fileCount"] += 1
        self.write_manifest(manifest)
        self.assert_refused("inventory does not match")


if __name__ == "__main__":
    unittest.main(verbosity=2)
