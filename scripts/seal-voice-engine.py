"""Seal or verify the offline engine inventory; never import torch or synthesize.

The manifest excludes itself, so inventory hashes have no self-reference. Source,
runtime and resource files are immutable inputs; this script writes only the
manifest through an atomic replacement after all structural checks succeed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat

SOURCE_COMMIT = "04a44eb977586645f33594b72af13a035a2056c6"
VARIANTS = ("v1", "v2", "v2Pro", "v2ProPlus", "v3", "v4", "v5dev", "v5turbo")
MANIFEST_NAME = "laidian-engine.json"


def is_reparse(path: Path) -> bool:
    info = path.lstat()
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def real_root(path: Path) -> Path:
    supplied = path.absolute()
    for candidate in (supplied, *supplied.parents):
        if candidate.exists() and is_reparse(candidate):
            raise ValueError("Engine cannot be sealed through a link or directory junction")
    root = supplied.resolve(strict=True)
    if not root.is_dir():
        raise ValueError("Engine root must be a directory")
    return root


def inventory(root: Path) -> list[Path]:
    files = []
    for directory, child_dirs, child_files in os.walk(root, followlinks=False):
        for name in child_dirs + child_files:
            item = Path(directory) / name
            if is_reparse(item):
                raise ValueError(f"Linked engine entry: {item.relative_to(root)}")
        for name in child_files:
            path = Path(directory) / name
            relative = path.relative_to(root)
            if relative.as_posix() == MANIFEST_NAME:
                continue
            if name.endswith((".part", ".partial")) or relative.as_posix() == MANIFEST_NAME + ".tmp":
                raise ValueError(f"Incomplete build artifact: {relative}")
            if "__pycache__" in relative.parts or path.suffix.lower() in {".pyc", ".pyo"}:
                raise ValueError(f"Generated Python cache cannot be distributed: {relative}")
            files.append(path)
    return sorted(files, key=lambda path: path.relative_to(root).as_posix())


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def package_version(root: Path, name: str) -> str:
    site = root / "runtime/Lib/site-packages"
    candidates = list(site.glob(name + "-*.dist-info/METADATA"))
    if len(candidates) != 1:
        raise ValueError(f"Expected exactly one installed {name} distribution")
    match = re.search(r"^Version: (.+)$", candidates[0].read_text(encoding="utf-8"), re.MULTILINE)
    if not match:
        raise ValueError(f"Missing {name} package version")
    return match.group(1).strip()


def required_files(variants: list[str]) -> set[str]:
    base = "GPT_SoVITS/pretrained_models/"
    required = {
        "LICENSE", "runtime/LICENSE.txt", "runtime/python.exe", "runtime/python311.dll", "runtime/python311.zip", "runtime/python311._pth",
        "vc-runtime-manifest.json", "licenses/Microsoft-VC-Runtime-License.docx",
        "runtime/msvcp140.dll", "runtime/msvcp140_1.dll", "runtime/vcomp140.dll",
        "runtime/vcruntime140.dll", "runtime/vcruntime140_1.dll",
        "runtime/ffmpeg.exe", "runtime/Lib/site-packages/torch/__init__.py",
        "runtime/Lib/site-packages/torchaudio/__init__.py", "GPT_SoVITS/TTS_infer_pack/TTS.py",
        "runtime/Lib/site-packages/setuptools/__init__.py", "runtime/Lib/site-packages/pkg_resources/__init__.py",
        "GPT_SoVITS/text/japanese.py", "GPT_SoVITS/text/english.py", "laidian_voice_resources.py",
        "laidian-cache-patches.json", "laidian-source-commit.txt", "laidian-source-trim.json", "requirements-voice.lock.txt",
        "licenses/SOURCES.txt", "licenses/FFmpeg-LGPL-3.0.txt", "licenses/FFmpeg-GPL-3.0.txt",
        "licenses/fastText-LICENSE.txt", "licenses/BERT-Apache-2.0.txt", "resource-manifest.json", "runtime/nltk_data/resources.json",
        "runtime/nltk_data/corpora/cmudict.zip", "runtime/nltk_data/taggers/averaged_perceptron_tagger.zip",
        base + "chinese-hubert-base/config.json", base + "chinese-hubert-base/preprocessor_config.json",
        base + "chinese-hubert-base/pytorch_model.bin", base + "chinese-roberta-wwm-ext-large/config.json",
        base + "chinese-roberta-wwm-ext-large/pytorch_model.bin", base + "chinese-roberta-wwm-ext-large/tokenizer.json",
        base + "chinese-roberta-wwm-ext-large/tokenizer_config.json", base + "chinese-roberta-wwm-ext-large/vocab.txt",
        base + "chinese-roberta-wwm-ext-large/special_tokens_map.json", base + "fast_langdetect/lid.176.bin",
    }
    for name in ("sys.dic", "matrix.bin", "char.bin", "unk.dic", "COPYING"):
        required.add("runtime/Lib/site-packages/pyopenjtalk/dictionary/" + name)
    for name in ("weights", "tagdict", "classes"):
        required.add("runtime/nltk_data/taggers/averaged_perceptron_tagger_eng/averaged_perceptron_tagger_eng." + name + ".json")
    selected = {name.casefold() for name in variants}
    if selected & {"v2pro", "v2proplus", "v5dev", "v5turbo"}:
        required.add(base + "sv/pretrained_eres2netv2w24s4ep4.ckpt")
    if "v3" in selected:
        required.update(base + name for name in ("s2Gv3.pth", "models--nvidia--bigvgan_v2_24khz_100band_256x/config.json",
                                                "models--nvidia--bigvgan_v2_24khz_100band_256x/bigvgan_generator.pt"))
    if "v4" in selected:
        required.update(base + "gsv-v4-pretrained/" + name for name in ("s2Gv4.pth", "vocoder.pth"))
    if selected & {"v5dev", "v5turbo"}:
        required.add(base + "gsv-v5-pretrained/vocoder.pth")
        if "v5dev" in selected:
            required.add(base + "gsv-v5-pretrained/s2Gv5dev.pth")
        if "v5turbo" in selected:
            required.add(base + "gsv-v5-pretrained/s2Gv5turbo.pth")
    return required


def validate_structure(root: Path, files: list[Path], variants: list[str]) -> None:
    present = {path.relative_to(root).as_posix() for path in files if path.stat().st_size > 0}
    missing = sorted(required_files(variants) - present)
    if missing:
        raise ValueError("Missing required inference resources: " + ", ".join(missing))
    for relative in ("runtime/python.exe", "runtime/ffmpeg.exe"):
        with (root / relative).open("rb") as stream:
            if stream.read(2) != b"MZ":
                raise ValueError(f"Invalid Windows executable: {relative}")
    if "Lib/site-packages" not in (root / "runtime/python311._pth").read_text(encoding="utf-8"):
        raise ValueError("Embedded Python cannot find bundled dependencies")


def normalize_variants(values: list[str]) -> list[str]:
    known = {name.casefold(): name for name in VARIANTS}
    unknown = set(value.casefold() for value in values) - known.keys()
    if unknown:
        raise ValueError("Unknown engine variants: " + ", ".join(sorted(unknown)))
    selected = {known[value.casefold()] for value in values}
    return [name for name in VARIANTS if name in selected]


def seal(root: Path, source_commit: str, variants: list[str], verified_variants: list[str]) -> dict:
    if not re.fullmatch(r"[0-9a-f]{40}", source_commit):
        raise ValueError("Source commit must be a full Git SHA-1")
    if not set(verified_variants).issubset(variants):
        raise ValueError("Verified variants must also be included in the distribution")
    files = inventory(root)
    validate_structure(root, files, variants)
    if (root / "laidian-source-commit.txt").read_text(encoding="utf-8").strip() != source_commit:
        raise ValueError("Source revision marker does not match the requested manifest revision")
    records = []
    for index, path in enumerate(files, 1):
        before = path.stat()
        size = before.st_size
        checksum = digest(path)
        after = path.stat()
        if after.st_size != size or after.st_mtime_ns != before.st_mtime_ns:
            raise ValueError("Engine changed while sealing; stop build/inference processes first")
        records.append({"path": path.relative_to(root).as_posix(), "size": size, "sha256": checksum})
        if index % 2000 == 0:
            print(f"Hashed {index}/{len(files)} files", flush=True)
    if files != inventory(root):
        raise ValueError("Engine inventory changed while sealing")
    manifest = {
        "schemaVersion": 1, "sourceCommit": source_commit, "pythonVersion": "3.11.9",
        "torchVersion": package_version(root, "torch"), "torchaudioVersion": package_version(root, "torchaudio"),
        "variants": variants, "verifiedVariants": verified_variants,
        "variantsMeaning": "Bundled inference dependencies; only verifiedVariants passed an actual model smoke test.",
        "fileCount": len(records), "totalBytes": sum(record["size"] for record in records), "files": records,
    }
    target = root / MANIFEST_NAME
    temporary = root / (MANIFEST_NAME + ".tmp")
    created = False
    try:
        with temporary.open("x", encoding="utf-8", newline="\n") as stream:
            created = True
            json.dump(manifest, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(target)
    finally:
        if created:
            temporary.unlink(missing_ok=True)
    return manifest


def verify(root: Path) -> dict:
    manifest = json.loads((root / MANIFEST_NAME).read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != 1:
        raise ValueError("Unsupported engine manifest")
    files = inventory(root)
    variants = normalize_variants(manifest["variants"])
    validate_structure(root, files, variants)
    actual = {path.relative_to(root).as_posix(): path for path in files}
    expected = {}
    for record in manifest["files"]:
        relative = record["path"]
        if relative in expected or relative == MANIFEST_NAME or relative not in actual:
            raise ValueError("Manifest contains duplicate, missing or unsafe entries")
        expected[relative] = record
    if actual.keys() != expected.keys():
        raise ValueError("Engine inventory differs from manifest")
    for name, path in actual.items():
        record = expected[name]
        if path.stat().st_size != record["size"] or digest(path) != record["sha256"]:
            raise ValueError(f"Engine integrity check failed: {name}")
    if manifest.get("fileCount") != len(actual) or manifest.get("totalBytes") != sum(item["size"] for item in expected.values()):
        raise ValueError("Manifest inventory totals do not match")
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", required=True, type=Path)
    parser.add_argument("--source-commit", default=SOURCE_COMMIT)
    parser.add_argument("--variants", nargs="+", default=list(VARIANTS))
    parser.add_argument("--verified-variants", nargs="*", default=[])
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    try:
        root = real_root(args.engine_root)
        manifest = verify(root) if args.verify else seal(root, args.source_commit,
            normalize_variants(args.variants), normalize_variants(args.verified_variants))
        print(f"{'Verified' if args.verify else 'Sealed'} {manifest['fileCount']} files, {manifest['totalBytes']} bytes; manifest excluded from its own hash inventory.")
    except (OSError, ValueError, KeyError) as error:
        parser.exit(1, f"Engine sealing failed: {error}\n")


if __name__ == "__main__":
    main()
