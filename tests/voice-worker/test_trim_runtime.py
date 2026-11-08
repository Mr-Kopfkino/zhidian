import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

PROJECT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("trim_voice_runtime", PROJECT / "scripts/trim-voice-runtime.py")
trim = importlib.util.module_from_spec(spec)
spec.loader.exec_module(trim)


class TrimRuntimeTest(unittest.TestCase):
    def setUp(self):
        base = PROJECT / ".build/worker-tests"
        base.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="runtime-trim-", dir=base)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        site = trim.SITE
        self.removed = [f"{site}/torch/lib/dnnl.lib", f"{site}/torch/include/api.h", "runtime/Scripts/pip.exe",
                        f"{site}/pip/__init__.py", f"{site}/wheel-0.48.0.dist-info/METADATA",
                        f"{site}/demo/__pycache__/module.cpython-311.pyc", f"{site}/demo/__pycache__/module.run-3.py311.nbc"]
        self.retained = [f"{site}/torch/lib/torch_cpu.dll", f"{site}/torch/lib/unreviewed.lib",
                         f"{site}/torch/testing/_internal/logging_tensor.py", f"{site}/torchgen/__init__.py",
                         f"{site}/pip/_vendor/vendor/licenses/MIT.txt", f"{site}/torch/include/LICENSE",
                         f"{site}/setuptools/__init__.py", f"{site}/pkg_resources/__init__.py",
                         f"{site}/numpy/tests/test_example.py", f"{site}/pyopenjtalk/dictionary/sys.dic",
                         f"{site}/demo/module.py", f"{site}/demo/__pycache__/sourceless.cpython-311.pyc"]
        for relative in self.removed + self.retained:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"fixture")

    def snapshot(self):
        return {p.relative_to(self.root).as_posix(): p.read_bytes() for p in self.root.rglob("*") if p.is_file()}

    def test_dry_run_reports_exact_files_without_changes(self):
        before = self.snapshot()
        report = trim.trim(self.root)
        self.assertTrue(report["dry_run"])
        self.assertEqual({f["path"] for f in report["files"]}, set(self.removed))
        self.assertEqual(report["bytes"], 7 * len(self.removed))
        self.assertEqual(self.snapshot(), before)

    def test_apply_preserves_dependencies_assets_licenses_and_is_idempotent(self):
        report = trim.trim(self.root, apply=True)
        self.assertFalse(report["dry_run"])
        self.assertEqual(set(self.snapshot()), set(self.retained))
        self.assertEqual(trim.trim(self.root, apply=True)["file_count"], 0)

    def test_reparse_path_aborts_before_any_deletion(self):
        before = self.snapshot()
        original = trim.is_reparse
        target = self.root / trim.SITE / "torch/include"
        with patch.object(trim, "is_reparse", side_effect=lambda p: p == target or original(p)):
            with self.assertRaisesRegex(ValueError, "linked/reparse"):
                trim.trim(self.root, apply=True)
        self.assertEqual(self.snapshot(), before)


if __name__ == "__main__":
    unittest.main()
