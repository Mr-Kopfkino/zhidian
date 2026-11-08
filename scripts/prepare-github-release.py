"""Prepare a clean source tree and GitHub-sized parts of an audited Portable ZIP.

Python 3.11+. No upload, credentials, user state, or private model inputs.
Run package-release.py first. Outputs must be a new directory under release/.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
PART_BYTES = 1_900_000_000
ROOT_FILES = ("README.md", "LICENSE", "THIRD_PARTY_NOTICES.md", ".gitignore",
              ".gitattributes", "Directory.Build.props", "NuGet.Config", "requirements-voice.lock.txt")
DOCS = ("KOTORI.md", "THEME.md", "VOICE_ENGINE.md", "GITHUB_RELEASE.md",
        "RELEASE_NOTES.md", "PUBLICATION_VALIDATION.md", "voice-inference-validation.json")
SKIP_DIRS = {"bin", "obj", "__pycache__", ".build", "data", "cache", "logs"}
SUFFIXES = {".cs", ".csproj", ".xaml", ".manifest", ".ico", ".py", ".ps1", ".cmd",
            ".json", ".txt", ".md", ".config"}


def real_path(path):
    path = path.absolute()
    for item in (path, *path.parents):
        if item.exists() and (item.is_symlink() or getattr(item.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT):
            raise ValueError("Linked paths are not allowed")
    return path.resolve()


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def source_files():
    paths = [PROJECT / name for name in ROOT_FILES]
    paths += [PROJECT / "docs" / name for name in DOCS]
    for name in ("src", "scripts", "tests", "licenses"):
        for current, dirs, files in os.walk(PROJECT / name, followlinks=False):
            for child in dirs + files:
                real_path(Path(current) / child)
            dirs[:] = sorted(d for d in dirs if d.casefold() not in SKIP_DIRS)
            for filename in sorted(files):
                path = Path(current) / filename
                if path.suffix.casefold() not in SUFFIXES:
                    raise ValueError("Unreviewed source file type: " + path.relative_to(PROJECT).as_posix())
                paths.append(path)
    return sorted(paths)


def copy_source(destination):
    # Reuse token patterns but never print matched secrets.
    import importlib.util
    spec = importlib.util.spec_from_file_location("release_audit", PROJECT / "scripts/audit-release.py")
    auditor = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(auditor)
    records = []
    local_markers = [str(PROJECT), str(Path.home())]
    for source in source_files():
        real_path(source)
        relative = source.relative_to(PROJECT).as_posix()
        raw = source.read_bytes()
        if len(raw) >= 50 * 1024 * 1024:
            raise ValueError("Unexpected large source file: " + relative)
        if source.suffix.casefold() != ".ico":
            text = raw.decode("utf-8-sig")
            for label, pattern in auditor.SECRET_PATTERNS.items():
                if pattern.search(text):
                    raise ValueError(label + " in source: " + relative)
            normalized = text.replace("\\\\", "\\").replace("\\", "/").casefold()
            if any(marker.replace("\\", "/").casefold() in normalized for marker in local_markers):
                raise ValueError("Local machine path in source: " + relative)
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        records.append(dict(path=relative, bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest()))
    return records


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    archive, output = real_path(args.archive), real_path(args.output)
    if not archive.is_relative_to(PROJECT / "release") or not output.is_relative_to(PROJECT / "release"):
        raise ValueError("Inputs and outputs must stay under release/")
    if output.exists():
        raise ValueError("Output already exists; use a fresh destination")
    version = ET.parse(PROJECT / "src/Qiban.Desktop/Qiban.Desktop.csproj").findtext(".//Version")
    package = json.loads(archive.with_suffix(".manifest.json").read_text(encoding="utf-8"))
    if package["version"] != version or package["bytes"] != archive.stat().st_size:
        raise ValueError("Package version or size mismatch")
    subprocess.run([sys.executable, "-I", "-B", str(PROJECT / "scripts/audit-release.py"), "--zip", str(archive)], check=True)
    with zipfile.ZipFile(archive) as zipped:
        if "coreclr.dll" not in zipped.namelist():
            raise ValueError("Only self-contained Portable packages are supported")
        unpacked = sum(entry.file_size for entry in zipped.infolist() if not entry.is_dir())
        file_count = sum(not entry.is_dir() for entry in zipped.infolist())
    if shutil.disk_usage(output.parent).free < archive.stat().st_size + 512 * 1024 * 1024:
        raise ValueError("Not enough space for release parts")
    output.mkdir(parents=True)
    source = output / "source"
    records = copy_source(source)
    (output / "source-manifest.json").write_text(json.dumps(dict(version=version, files=records), indent=2), encoding="utf-8")
    source_zip = output / f"Zhidian-v{version}-source.zip"
    with zipfile.ZipFile(source_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
        for record in records:
            zipped.write(source / record["path"], f"Zhidian-v{version}-source/" + record["path"])
    with zipfile.ZipFile(source_zip) as zipped:
        if zipped.testzip():
            raise ValueError("Source archive integrity failure")
    (output / "SOURCE_SHA256SUMS.txt").write_text(
        f"{sha256(source_zip)}  {source_zip.name}\n"
        f"{sha256(output / 'source-manifest.json')}  source-manifest.json\n", encoding="ascii")
    assets = output / "release-assets"
    assets.mkdir()
    archive_name = f"Zhidian-v{version}-win-x64.zip"
    parts = []
    total_hash = hashlib.sha256()
    with archive.open("rb") as stream:
        remaining = archive.stat().st_size
        index = 1
        while remaining:
            size = min(PART_BYTES, remaining)
            path = assets / f"{archive_name}.{index:03}"
            digest = hashlib.sha256()
            with path.open("xb") as target:
                pending = size
                while pending:
                    block = stream.read(min(pending, 8 * 1024 * 1024))
                    if not block:
                        raise ValueError("Archive truncated during splitting")
                    target.write(block)
                    digest.update(block)
                    total_hash.update(block)
                    pending -= len(block)
            if sha256(path) != digest.hexdigest():
                raise ValueError("Part read-back verification failed")
            parts.append(dict(name=path.name, bytes=size, sha256=digest.hexdigest()))
            remaining -= size
            index += 1
            print(f"Verified part {path.name}: {size} bytes", flush=True)
        if stream.read(1) or total_hash.hexdigest() != package["sha256"]:
            raise ValueError("Full archive SHA256 mismatch")
    manifest = dict(version=version, directory=f"Zhidian-v{version}-win-x64",
                    archive=dict(name=archive_name, bytes=archive.stat().st_size,
                                 sha256=total_hash.hexdigest(), unpackedBytes=unpacked, fileCount=file_count),
                    parts=parts)
    (assets / "release-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    for name in ("Extract-Zhidian.ps1", "Extract-Zhidian.cmd"):
        shutil.copyfile(PROJECT / "scripts/release-tools" / name, assets / name)
    (assets / "README-DOWNLOAD.txt").write_text(
        "致电 v" + version + " / Windows 10、11 x64 完整便携版\n\n"
        "下载本目录全部文件，保持同目录和原文件名。\n"
        "双击 Extract-Zhidian.cmd，等待校验、合并和解压完成。\n"
        "进入 Zhidian-v" + version + "-win-x64 文件夹，运行 Zhidian.exe。\n"
        "无需安装 .NET、Python 或 CUDA Toolkit。不要在 ZIP 内启动。\n"
        "下载完成后，解压前仍需至少 22 GB 可用空间；解压后可删除分卷。\n"
        "首次使用需在设置中填写聊天服务地址、模型及自己的密钥。\n"
        "内置语音还需自行准备有权使用的角色权重和参考音频。\n"
        "旧版用户先关闭程序，再使用包内 Upgrade-Data.ps1 迁移数据。\n"
        "MIT 仅适用于本项目原创代码，第三方许可见包内说明。\n", encoding="utf-8-sig")
    sums = [f"{sha256(path)}  {path.name}" for path in sorted(assets.iterdir())]
    (assets / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n", encoding="ascii")
    shutil.copyfile(PROJECT / "docs/RELEASE_NOTES.md", output / "RELEASE_NOTES.md")
    shutil.copyfile(PROJECT / "docs/GITHUB_RELEASE.md", output / "发布指南.md")
    print(json.dumps(dict(version=version, sourceFiles=len(records), parts=len(parts),
                          archiveSha256=total_hash.hexdigest(), unpackedBytes=unpacked)), flush=True)


if __name__ == "__main__":
    main()
