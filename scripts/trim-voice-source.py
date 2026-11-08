"""Trim the pinned GPT-SoVITS source for Laidian's inference-only distribution.

Run at build time with --engine-root. No imports of torch or model loading occur.
Only reviewed Python source is removed; runtime, models, dictionaries, licensing
files and all inference/vocoder modules remain. Source drift fails before writes.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
from pathlib import Path
import stat

UPSTREAM_COMMIT = "04a44eb977586645f33594b72af13a035a2056c6"
MARKER = "# LAIDIAN_INFERENCE_ONLY_V1"
WRAPPER = "GPT_SoVITS/AR/models/t2s_lightning_module.py"
DECODER = "GPT_SoVITS/AR/models/t2s_model.py"
CHINESE = "GPT_SoVITS/text/chinese2.py"
CHINESE_V1 = "GPT_SoVITS/text/chinese.py"
TONE = "GPT_SoVITS/text/tone_sandhi.py"
# Hashes use UTF-8 with LF newlines, independent of git's checkout line endings.
SOURCE_SHA256 = {
    WRAPPER: "6a4d3173181000a6012bbdf055ca8a29ea6e48ce5a09605a579b509a548f8588",
    DECODER: "e0f0e9fb97e2c0d8f4f80ec4d4c5427607242923770bfa81e776e18121f7b7a9",
    CHINESE: "a33a7fced1496c034522e46be2702c49f71f93cf4bc8e2bd216fab5d3fc08ae5",
    CHINESE_V1: "9ceae1540e10a32ec8d874b10d0b07912b208c81db492938604c4540771703da",
    TONE: "b651a9cf7a94b66401101876616a85479bb2fb266aa41f85423b9371bbe4137d",
}
PATCHED_SHA256 = {
    WRAPPER: "26671d0ae93ce0c057288a57b3099e75f5b653e462ad73c72cb69f0306f728e9",
    DECODER: "769608e11c3c60a60fd897a0bb36771311ccdb94ea9a007e8c6e8c936bc02e7c",
    CHINESE: "58ef83380ec40af6de92e913f6f65c1a990d99754fd4497b52642adcaf945d85",
    CHINESE_V1: "126daf9b682a163da6dfa9c8bdd8b6a1337401ba3e68648d00afd5a8f2fc0010",
    TONE: "9f0efe2bc6ce2373983fc9bb25ebb64c67c8a84efaaa65a3cf7eb6f5f330f566",
}
SOURCE_DIRECTORIES = (
    "GPT_SoVITS/prepare_datasets",
    "GPT_SoVITS/AR/data",
    "tools/asr",
    "tools/uvr5",
    "tools/denoise-model",
)
SOURCE_FILES = (
    "GPT_SoVITS/s1_train.py",
    "GPT_SoVITS/s2_train.py",
    "GPT_SoVITS/s2_train_v3.py",
    "GPT_SoVITS/s2_train_v3_lora.py",
    "GPT_SoVITS/inference_cli.py",
    "GPT_SoVITS/inference_gui.py",
    "GPT_SoVITS/inference_webui.py",
    "GPT_SoVITS/inference_webui_fast.py",
    "GPT_SoVITS/export_torch_script.py",
    "GPT_SoVITS/export_torch_script_v3v4.py",
    "GPT_SoVITS/onnx_export.py",
    "GPT_SoVITS/stream_v2pro.py",
    "tools/cmd-denoise.py",
    "tools/slice_audio.py",
    "tools/slicer2.py",
    "tools/subfix_webui.py",
)


def digest(source: str) -> str:
    return hashlib.sha256(source.encode("utf-8")).hexdigest()


def patch_wrapper(source: str) -> str:
    # Retain the upstream attribution and the model.* checkpoint namespace.
    credits = "\n".join(source.splitlines()[:2])
    return credits + '''
# LAIDIAN_INFERENCE_ONLY_V1
import torch
from AR.models.t2s_model import Text2SemanticDecoder


class Text2SemanticLightningModule(torch.nn.Module):
    """Checkpoint-compatible inference wrapper; training is not bundled."""

    def __init__(self, config, output_dir=None, is_train=False):
        super().__init__()
        if is_train:
            raise NotImplementedError("This distribution supports inference only")
        self.config = config
        self.top_k = 3
        self.model = Text2SemanticDecoder(config=config, top_k=self.top_k)

    def training_step(self, *args, **kwargs):
        raise NotImplementedError("This distribution supports inference only")

    def validation_step(self, *args, **kwargs):
        raise NotImplementedError("This distribution supports inference only")

    def configure_optimizers(self):
        raise NotImplementedError("This distribution supports inference only")
'''


def patch_decoder(source: str) -> str:
    tree = ast.parse(source)
    decoder = next(n for n in tree.body if isinstance(n, ast.ClassDef) and n.name == "Text2SemanticDecoder")
    imports = [n for n in tree.body if isinstance(n, ast.ImportFrom) and n.module == "torchmetrics.classification"]
    constructors = [n for n in decoder.body if isinstance(n, ast.FunctionDef) and n.name == "__init__"]
    metrics = [n for n in constructors[0].body if isinstance(n, ast.Assign)
               and any(isinstance(t, ast.Attribute) and isinstance(t.value, ast.Name)
                       and t.value.id == "self" and t.attr == "ar_accuracy_metric" for t in n.targets)]
    training = [n for n in decoder.body if isinstance(n, ast.FunctionDef) and n.name in {"forward", "forward_old"}]
    if len(imports) != 1 or len(constructors) != 1 or len(metrics) != 1 or len(training) != 2:
        raise ValueError("Unexpected decoder training structure")
    # Edit only these line ranges: helpers, other forward methods and inference
    # methods retain their original source exactly, including decorators.
    edits = [(imports[0].lineno - 1, imports[0].end_lineno, MARKER + "\n"),
             (metrics[0].lineno - 1, metrics[0].end_lineno, "        self.ar_accuracy_metric = None\n")]
    for method in training:
        edits.append((method.body[0].lineno - 1, method.end_lineno,
                      '        raise NotImplementedError("This distribution supports inference only")\n'))
    lines = source.splitlines(keepends=True)
    for start, end, replacement in sorted(edits, reverse=True):
        lines[start:end] = [replacement]
    return "".join(lines)


def patch_chinese(source: str) -> str:
    original = "is_g2pw = True  # True if is_g2pw_str.lower() == 'true' else False"
    if source.count(original) != 1:
        raise ValueError("Unexpected Chinese frontend structure")
    # Use upstream's existing pypinyin branch by default. G2PW is explicitly
    # opt-in, consistent with tools/portable_runtime.py; no ONNX model needed.
    return patch_jieba(source).replace(original, MARKER + '\nis_g2pw = os.environ.get("G2PW", "0").strip().lower() in {"1", "true", "yes"}')


def patch_jieba(source: str) -> str:
    # jieba_fast forks jieba's Python API. Keep the original local alias and
    # pronunciation algorithms, using the portable pure-Python implementation.
    before = "import jieba_fast\n"
    posseg = "import jieba_fast.posseg as psg"
    if source.count(before) != 1 or source.count(posseg) != 1:
        raise ValueError("Unexpected jieba frontend imports")
    return source.replace(before, MARKER + "\nimport jieba as jieba_fast\n").replace(posseg, "import jieba.posseg as psg")


def patch_tone(source: str) -> str:
    before = "import jieba_fast as jieba"
    if source.count(before) != 1:
        raise ValueError("Unexpected tone-sandhi tokenizer import")
    return source.replace(before, MARKER + "\nimport jieba")


PATCHERS = {WRAPPER: patch_wrapper, DECODER: patch_decoder, CHINESE: patch_chinese,
            CHINESE_V1: patch_jieba, TONE: patch_tone}


def is_reparse(path: Path) -> bool:
    info = path.lstat()
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0)
                                           & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def checked_path(root: Path, relative: str) -> Path:
    parts = Path(relative).parts
    if Path(relative).is_absolute() or any(part in {"..", "."} for part in parts):
        raise ValueError(f"Unsafe relative path: {relative}")
    target = root
    for part in parts:
        target = target / part
        if os.path.lexists(target) and is_reparse(target):
            raise ValueError(f"Refusing linked/reparse path: {relative}")
    if not target.resolve().is_relative_to(root):
        raise ValueError(f"Path escapes engine root: {relative}")
    return target


def removable_source(path: Path) -> bool:
    name = path.name.casefold()
    return (not name.startswith(("license", "licence", "copying", "notice", "copyright"))
            and path.suffix.casefold() in {".py", ".pyi", ".pyc", ".pyo"})


def trim(engine_root: Path, dry_run: bool = False) -> dict:
    supplied = engine_root.absolute()
    root = supplied.resolve(strict=True)
    if not root.is_dir() or is_reparse(supplied) or supplied != root:
        raise ValueError("Engine root must be a real directory, not a link/junction")
    changes = []
    # Preflight every edited source and every removed tree before changing any
    # file. A different upstream revision must be reviewed, not patched blindly.
    for relative, patcher in PATCHERS.items():
        path = checked_path(root, relative)
        source = path.read_text(encoding="utf-8")
        current_hash = digest(source)
        if current_hash == PATCHED_SHA256[relative]:
            continue
        if current_hash != SOURCE_SHA256[relative]:
            raise ValueError(f"Unreviewed source revision: {relative}")
        patched = patcher(source)
        ast.parse(patched)
        if digest(patched) != PATCHED_SHA256[relative]:
            raise ValueError(f"Unexpected patch output: {relative}")
        changes.append((path, patched))
    removed_files, directories = set(), set()
    for relative in SOURCE_DIRECTORIES:
        directory = checked_path(root, relative)
        if not directory.exists():
            continue
        if not directory.is_dir():
            raise ValueError(f"Expected source directory: {relative}")
        for current, child_dirs, files in os.walk(directory, followlinks=False):
            current = Path(current)
            directories.add(current)
            for name in child_dirs + files:
                path = checked_path(root, (current / name).relative_to(root).as_posix())
                if path.is_file() and removable_source(path):
                    removed_files.add(path)
    for relative in SOURCE_FILES:
        path = checked_path(root, relative)
        if path.exists():
            if not path.is_file():
                raise ValueError(f"Expected source file: {relative}")
            removed_files.add(path)
    report = {
        "upstream_commit": UPSTREAM_COMMIT,
        "dry_run": dry_run,
        "modified": [p.relative_to(root).as_posix() for p, _ in changes],
        "deleted": sorted(p.relative_to(root).as_posix() for p in removed_files),
        "removed_empty_directories": [],
    }
    if dry_run:
        return report
    for path, patched in changes:
        path.write_text(patched, encoding="utf-8", newline="\n")
    for path in sorted(removed_files):
        path.unlink()
    for directory in sorted(directories, key=lambda p: len(p.parts), reverse=True):
        if not any(directory.iterdir()):
            directory.rmdir()
            report["removed_empty_directories"].append(directory.relative_to(root).as_posix())
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", required=True, type=Path)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        print(json.dumps(trim(args.engine_root, args.dry_run), ensure_ascii=False, indent=2))
    except (OSError, ValueError, SyntaxError) as error:
        parser.exit(1, f"Source trim aborted: {error}\n")


if __name__ == "__main__":
    main()
