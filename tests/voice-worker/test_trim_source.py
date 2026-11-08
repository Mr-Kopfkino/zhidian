import ast
import importlib.util
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch

PROJECT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("trim_voice_source", PROJECT / "scripts/trim-voice-source.py")
trim = importlib.util.module_from_spec(spec)
spec.loader.exec_module(trim)

SOURCES = {
    trim.WRAPPER: "# upstream attribution\n# reference\nclass Text2SemanticLightningModule: pass\n",
    trim.DECODER: '''from torchmetrics.classification import MulticlassAccuracy
class Helper:
    def forward(self, x):
        return x + 1
class Text2SemanticDecoder(nn.Module):
    def __init__(self, config, norm_first=False, top_k=3):
        self.model = config
        self.ar_accuracy_metric = MulticlassAccuracy(
            100, top_k=top_k,
        )
        self.inference_state = 42
    def forward(self, x):
        return self.ar_accuracy_metric(x)
    def forward_old(self, x):
        return self.ar_accuracy_metric(x)
    @torch.no_grad()
    def infer(self, x):
        # Inference source must not change.
        return self.model(x)
''',
    trim.CHINESE: '''import os
import jieba_fast
import jieba_fast.posseg as psg
is_g2pw = True  # True if is_g2pw_str.lower() == 'true' else False
if is_g2pw:
    import onnxruntime
else:
    import pypinyin
def g2p(text):
    return text
''',
    trim.CHINESE_V1: '''import jieba_fast
import jieba_fast.posseg as psg
def g2p(text):
    return list(psg.cut(text))
''',
    trim.TONE: '''# upstream license
import jieba_fast as jieba
def split(text):
    return list(jieba.cut_for_search(text))
''',
}


class TrimSourceTest(unittest.TestCase):
    def setUp(self):
        base = PROJECT / ".build/worker-tests"
        base.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="trim-", dir=base)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for relative, source in SOURCES.items():
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(source, encoding="utf-8", newline="\n")
        self.addCleanup(patch.stopall)
        patch.object(trim, "SOURCE_SHA256", {r: trim.digest(s) for r, s in SOURCES.items()}).start()
        patch.object(trim, "PATCHED_SHA256", {r: trim.digest(trim.PATCHERS[r](s)) for r, s in SOURCES.items()}).start()
        self.write("tools/asr/worker.py", "# unused ASR source\n")
        self.write("tools/asr/LICENSE", "license to retain")
        self.write("tools/asr/user-model.pth", "model to retain")
        self.write("GPT_SoVITS/prepare_datasets/prepare.py", "# unused training source\n")
        self.write("GPT_SoVITS/s1_train.py", "# unused training entrypoint\n")
        self.write("GPT_SoVITS/pretrained_models/model.pth", "pretrained weight")
        self.write("GPT_SoVITS/BigVGAN/bigvgan.py", "# inference source\n")
        self.write("runtime/Lib/site-packages/package.py", "# runtime dependency\n")

    def write(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def snapshot(self):
        return {p.relative_to(self.root).as_posix(): p.read_bytes() for p in self.root.rglob("*") if p.is_file()}

    def test_trim_preserves_assets_licenses_inference_and_is_idempotent(self):
        before = self.snapshot()
        report = trim.trim(self.root)
        self.assertEqual(set(report["modified"]), set(SOURCES))
        self.assertEqual(set(report["deleted"]), {"tools/asr/worker.py", "GPT_SoVITS/prepare_datasets/prepare.py", "GPT_SoVITS/s1_train.py"})
        after = self.snapshot()
        for path, content in before.items():
            if path not in report["modified"] and path not in report["deleted"]:
                self.assertEqual(after[path], content)
        decoder = (self.root / trim.DECODER).read_text()
        original_tree, modified_tree = ast.parse(SOURCES[trim.DECODER]), ast.parse(decoder)
        self.assertEqual(ast.get_source_segment(SOURCES[trim.DECODER], original_tree.body[1]),
                         ast.get_source_segment(decoder, modified_tree.body[0]))
        self.assertEqual(ast.get_source_segment(SOURCES[trim.DECODER], original_tree.body[2].body[-1]),
                         ast.get_source_segment(decoder, modified_tree.body[1].body[-1]))
        self.assertNotIn("MulticlassAccuracy", decoder)
        self.assertEqual(decoder.count("raise NotImplementedError"), 2)
        wrapper = (self.root / trim.WRAPPER).read_text()
        self.assertIn("class Text2SemanticLightningModule(torch.nn.Module)", wrapper)
        self.assertIn("self.model = Text2SemanticDecoder", wrapper)
        self.assertTrue(wrapper.startswith("# upstream attribution\n# reference\n"))
        again = trim.trim(self.root)
        self.assertEqual(again["modified"], [])
        self.assertEqual(again["deleted"], [])
        self.assertEqual(self.snapshot(), after)

    def test_unknown_revision_aborts_before_modification_or_deletion(self):
        self.write(trim.CHINESE, SOURCES[trim.CHINESE] + "# unknown upstream revision\n")
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, "Unreviewed source revision"):
            trim.trim(self.root)
        self.assertEqual(before, self.snapshot())

    def test_altered_patched_source_fails_closed(self):
        trim.trim(self.root)
        path = self.root / trim.DECODER
        path.write_text(path.read_text() + "# changed after patch\n")
        before = self.snapshot()
        with self.assertRaisesRegex(ValueError, "Unreviewed source revision"):
            trim.trim(self.root)
        self.assertEqual(before, self.snapshot())

    def test_dry_run_does_not_modify_any_file(self):
        before = self.snapshot()
        report = trim.trim(self.root, dry_run=True)
        self.assertTrue(report["dry_run"])
        self.assertEqual(len(report["modified"]), len(SOURCES))
        self.assertEqual(before, self.snapshot())

    def test_crlf_checkout_has_same_verified_patch(self):
        for relative, source in SOURCES.items():
            (self.root / relative).write_bytes(source.replace("\n", "\r\n").encode("utf-8"))
        trim.trim(self.root)
        for relative, source in SOURCES.items():
            self.assertEqual((self.root / relative).read_bytes(), trim.PATCHERS[relative](source).encode("utf-8"))

    def test_reparse_point_is_rejected_before_writes(self):
        before = self.snapshot()
        original = trim.is_reparse
        with patch.object(trim, "is_reparse", side_effect=lambda p: p == self.root / "tools/asr" or original(p)):
            with self.assertRaisesRegex(ValueError, "linked/reparse"):
                trim.trim(self.root)
        self.assertEqual(before, self.snapshot())

    def test_lightweight_chinese_is_default_and_g2pw_is_explicit(self):
        source = trim.patch_chinese(SOURCES[trim.CHINESE])
        assignment = next(n for n in ast.parse(source).body if isinstance(n, ast.Assign))
        expression = compile(ast.Expression(assignment.value), "<g2pw-flag>", "eval")
        for value, enabled in ((None, False), ("0", False), ("False", False), ("1", True), ("TRUE", True)):
            with patch.dict(os.environ, {}, clear=True):
                if value is not None:
                    os.environ["G2PW"] = value
                self.assertEqual(eval(expression, {"os": os}), enabled)
        self.assertIn("else:\n    import pypinyin", source)

    def test_jieba_alias_keeps_tokenizer_calls_and_needs_no_fast_extension(self):
        jieba, posseg = types.ModuleType("jieba"), types.ModuleType("jieba.posseg")
        jieba.posseg = posseg
        jieba.cut_for_search = lambda text: [text, "search"]
        posseg.cut = lambda text: [(text, "noun")]
        with patch.dict("sys.modules", {"jieba": jieba, "jieba.posseg": posseg, "jieba_fast": None}):
            chinese, tone = {}, {}
            exec(trim.patch_jieba(SOURCES[trim.CHINESE_V1]), chinese)
            exec(trim.patch_tone(SOURCES[trim.TONE]), tone)
            self.assertIs(chinese["jieba_fast"], jieba)
            self.assertIs(chinese["psg"], posseg)
            self.assertEqual(chinese["g2p"]("中文"), [("中文", "noun")])
            self.assertEqual(tone["split"]("中文"), ["中文", "search"])


if __name__ == "__main__":
    unittest.main()
