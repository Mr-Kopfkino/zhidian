import contextlib
import importlib.util
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

PROJECT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("prepare_voice_engine", PROJECT / "scripts/prepare-voice-engine.py")
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class PrepareTest(unittest.TestCase):
    def setUp(self):
        base = PROJECT / ".build/worker-tests"
        base.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="patch-", dir=base)
        self.root = Path(self.temporary.name)
        self.engine, self.work = self.root / "engine", self.root / "cache/session"
        text = self.engine / "GPT_SoVITS/text"
        (text / "LangSegmenter").mkdir(parents=True)
        (text / "ja_userdic").mkdir()
        (text / "ja_userdic/userdict.csv").write_text("fixture-dictionary", encoding="utf-8")
        (text / "japanese.py").write_text("# upstream credit\nUSERDIC_BIN_PATH = 'user.dict'\nmecab_dict_index = None\nfrom text.symbols import punctuation\nvalue = 123\n")
        (text / "english.py").write_text('import os\ncurrent_file_path = os.path.dirname(__file__)\nCACHE_PATH = os.path.join(current_file_path, "engdict_cache.pickle")\n')
        (text / "LangSegmenter/langsegmenter.py").write_text('from pathlib import Path\nimport fast_langdetect\nfast_langdetect.infer._default_detector = fast_langdetect.infer.LangDetector(fast_langdetect.infer.LangDetectConfig(cache_dir=Path(__file__).parent.parent.parent / "pretrained_models" / "fast_langdetect"))\n')

    def tearDown(self):
        self.temporary.cleanup()

    def helper(self):
        spec = importlib.util.spec_from_file_location("fixture_voice_resources", self.engine / "laidian_voice_resources.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def test_patches_are_idempotent_and_keep_algorithm(self):
        self.assertEqual(len(prepare.prepare(self.engine)), 3)
        before = {p.relative_to(self.engine): p.read_bytes() for p in self.engine.rglob("*") if p.is_file()}
        self.assertEqual(prepare.prepare(self.engine), [])
        after = {p.relative_to(self.engine): p.read_bytes() for p in self.engine.rglob("*") if p.is_file()}
        self.assertEqual(before, after)
        japanese = (self.engine / "GPT_SoVITS/text/japanese.py").read_text()
        self.assertTrue(japanese.startswith("# upstream credit"))
        self.assertIn("value = 123", japanese)
        self.assertNotIn("USERDIC_BIN_PATH", japanese)

    def test_unknown_source_aborts_before_any_source_write(self):
        english = self.engine / "GPT_SoVITS/text/english.py"
        english.write_text("# unsupported revision\n")
        japanese = self.engine / "GPT_SoVITS/text/japanese.py"
        before = japanese.read_bytes()
        with self.assertRaises(ValueError): prepare.prepare(self.engine)
        self.assertEqual(japanese.read_bytes(), before)
        self.assertFalse((self.engine / "laidian_voice_resources.py").exists())

    def test_japanese_native_outputs_stay_in_session_and_reuse(self):
        prepare.prepare(self.engine)
        helper = self.helper()
        dictionary = self.engine / "runtime/Lib/site-packages/pyopenjtalk/dictionary"
        dictionary.mkdir(parents=True)
        for name in ("sys.dic", "matrix.bin", "char.bin", "unk.dic"): (dictionary / name).write_bytes(b"fixture")
        calls = []
        def build(csv, binary):
            calls.append(Path(binary).resolve())
            self.assertTrue(Path(csv).resolve().is_relative_to(self.work))
            Path(binary).write_bytes(b"compiled-user-dictionary")
        module = types.SimpleNamespace(OPEN_JTALK_DICT_DIR=str(dictionary).encode(), mecab_dict_index=build,
                                       update_global_jtalk_with_user_dict=lambda path: self.assertTrue(Path(path).resolve().is_relative_to(self.work)))
        with patch.dict(os.environ, {"LAIDIAN_VOICE_WORK": str(self.work)}), contextlib.chdir(self.engine):
            helper.prepare_japanese(module, self.engine / "GPT_SoVITS/text/japanese.py")
            helper.prepare_japanese(module, self.engine / "GPT_SoVITS/text/japanese.py")
        self.assertEqual(len(calls), 1)
        self.assertFalse((self.engine / "GPT_SoVITS/text/ja_userdic/user.dict").exists())
        self.assertEqual((self.work / "ja_userdic/user.dict").read_bytes(), b"compiled-user-dictionary")

    def test_language_assets_fail_offline_and_english_cache_is_local(self):
        prepare.prepare(self.engine)
        helper = self.helper()
        model = self.engine / "GPT_SoVITS/pretrained_models/fast_langdetect"
        with self.assertRaises(FileNotFoundError): helper.require_language_model(model)
        model.mkdir(parents=True)
        (model / "lid.176.bin").write_bytes(b"fixture")
        self.assertEqual(helper.require_language_model(model), model)
        source = self.engine / "source.pickle"; source.write_bytes(b"read-only-dictionary")
        with patch.dict(os.environ, {"LAIDIAN_VOICE_WORK": str(self.work)}):
            cache = Path(helper.cached_resource("engdict_cache.pickle", source))
        self.assertEqual(cache.parent, self.work)
        self.assertEqual(cache.read_bytes(), source.read_bytes())


if __name__ == "__main__":
    unittest.main(verbosity=2)
