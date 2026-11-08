"""Apply the small, repeatable cache/offline patches to a GPT-SoVITS engine tree.

No downloads or inference are performed. Run after extracting the pinned upstream
source and before packaging. Third-party licensing files are retained unchanged.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path

MARKER = "# LAIDIAN_OFFLINE_CACHE_V1"
HELPER = r'''"""Local resources for the bundled inference-only GPT-SoVITS engine."""
import hashlib
import os
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parent

def work_dir():
    value = os.environ.get("LAIDIAN_VOICE_WORK")
    if not value:
        raise RuntimeError("Laidian voice session is required")
    work = Path(value).resolve()
    work.mkdir(parents=True, exist_ok=True)
    return work

def cached_resource(name, source):
    target = work_dir() / name
    if not target.is_file() and Path(source).is_file():
        temporary = target.with_suffix(target.suffix + ".part")
        shutil.copyfile(source, temporary)
        os.replace(temporary, target)
    return str(target)

def _native_path(path):
    path = Path(path).resolve()
    # OpenJTalk on Windows may reject non-ASCII absolute paths. Both resources
    # and the session share the app root, so a relative path avoids that prefix.
    value = os.path.relpath(path, Path.cwd()) if os.name == "nt" else str(path)
    if os.name == "nt" and not re.fullmatch(r"[A-Za-z0-9_/\\:.\- ]+", value):
        raise RuntimeError("OpenJTalk cannot represent this relative path")
    return value

def prepare_japanese(module, source_file):
    dictionary = Path(os.fsdecode(module.OPEN_JTALK_DICT_DIR)).resolve()
    if not dictionary.is_relative_to(ROOT):
        raise RuntimeError("Japanese dictionary must be bundled with the engine")
    if not all((dictionary / name).is_file() for name in ("sys.dic", "matrix.bin", "char.bin", "unk.dic")):
        raise FileNotFoundError("Bundled OpenJTalk dictionary is incomplete")
    module.OPEN_JTALK_DICT_DIR = _native_path(dictionary).encode("utf-8")
    source = Path(source_file).parent / "ja_userdic/userdict.csv"
    if not source.is_file():
        return
    directory = work_dir() / "ja_userdic"
    directory.mkdir(parents=True, exist_ok=True)
    csv, binary, digest_file = (directory / name for name in ("userdict.csv", "user.dict", "userdict.sha256"))
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    if not binary.is_file() or not digest_file.is_file() or digest_file.read_text() != digest:
        shutil.copyfile(source, csv)
        # Native code writes only to the session directory, never the engine tree.
        module.mecab_dict_index(_native_path(csv), _native_path(binary))
        if not binary.is_file():
            raise RuntimeError("OpenJTalk did not generate the user dictionary")
        digest_file.write_text(digest, encoding="ascii")
    module.update_global_jtalk_with_user_dict(_native_path(binary))

def require_language_model(directory):
    directory = Path(directory).resolve()
    if not directory.is_relative_to(ROOT) or not (directory / "lid.176.bin").is_file():
        raise FileNotFoundError("Bundled fastText language model is missing")
    return directory
'''


def checked_source(root: Path, relative: str) -> tuple[Path, str]:
    path = root / relative
    if path.is_symlink() or not path.resolve().is_relative_to(root):
        raise ValueError("Engine source path escapes the requested directory")
    return path, path.read_text(encoding="utf-8")


def prepare(root: Path) -> list[str]:
    root = root.resolve(strict=True)
    changes: list[tuple[Path, str, str]] = []
    path, source = checked_source(root, "GPT_SoVITS/text/japanese.py")
    if MARKER not in source:
        split = "from text.symbols import punctuation"
        if source.count(split) != 1 or "USERDIC_BIN_PATH" not in source or "mecab_dict_index" not in source:
            raise ValueError("Unsupported upstream Japanese frontend; review before patching")
        replacement = (source.splitlines()[0] + "\n" + MARKER + "\nimport re\nimport os\nimport pyopenjtalk\n"
                       "from laidian_voice_resources import prepare_japanese\n"
                       "prepare_japanese(pyopenjtalk, __file__)\n\n" + split + source.split(split, 1)[1])
        changes.append((path, source, replacement))

    path, source = checked_source(root, "GPT_SoVITS/text/english.py")
    if MARKER not in source:
        before = 'CACHE_PATH = os.path.join(current_file_path, "engdict_cache.pickle")'
        if source.count(before) != 1:
            raise ValueError("Unsupported upstream English frontend; review before patching")
        replacement = source.replace(before, MARKER + '\nfrom laidian_voice_resources import cached_resource\n'
                                     'CACHE_PATH = cached_resource("engdict_cache.pickle", os.path.join(current_file_path, "engdict_cache.pickle"))')
        changes.append((path, source, replacement))

    path, source = checked_source(root, "GPT_SoVITS/text/LangSegmenter/langsegmenter.py")
    if MARKER not in source:
        before = 'fast_langdetect.infer._default_detector = fast_langdetect.infer.LangDetector(fast_langdetect.infer.LangDetectConfig(cache_dir=Path(__file__).parent.parent.parent / "pretrained_models" / "fast_langdetect"))'
        if source.count(before) != 1:
            raise ValueError("Unsupported upstream language detector; review before patching")
        replacement = source.replace(before, MARKER + '\nfrom laidian_voice_resources import require_language_model\n'
                                     'fast_langdetect.infer._default_detector = fast_langdetect.infer.LangDetector(fast_langdetect.infer.LangDetectConfig(cache_dir=require_language_model(Path(__file__).parent.parent.parent / "pretrained_models" / "fast_langdetect")))')
        changes.append((path, source, replacement))

    # Validate every patch before replacing any source. A repeat run is a no-op.
    for path, _, replacement in changes:
        compile(replacement, str(path), "exec")
    compile(HELPER, "laidian_voice_resources.py", "exec")
    for path, _, replacement in changes:
        path.write_text(replacement, encoding="utf-8")
    helper = root / "laidian_voice_resources.py"
    if not helper.is_file() or helper.read_text(encoding="utf-8") != HELPER:
        helper.write_text(HELPER, encoding="utf-8")
    if changes:
        manifest = root / "laidian-cache-patches.json"
        manifest.write_text(json.dumps({"patch": "offline-cache-v1", "files": [
            {"path": path.relative_to(root).as_posix(), "beforeSha256": hashlib.sha256(before.encode()).hexdigest(),
             "afterSha256": hashlib.sha256(after.encode()).hexdigest()} for path, before, after in changes
        ]}, indent=2) + "\n", encoding="utf-8")
    return [path.relative_to(root).as_posix() for path, _, _ in changes]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", required=True, type=Path)
    arguments = parser.parse_args()
    result = prepare(arguments.engine_root)
    print(f"Prepared {len(result)} offline/cache source patches.")
