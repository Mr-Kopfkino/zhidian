"""Build and verify a portable ZIP from a staged release without user data."""
import argparse
import hashlib
import json
from pathlib import Path
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--stage", required=True, type=Path)
parser.add_argument("--output", required=True, type=Path)
args = parser.parse_args()
project = Path(__file__).resolve().parents[1]
version = ET.parse(project / "src/Qiban.Desktop/Qiban.Desktop.csproj").findtext(".//Version")
if not version or not version.strip():
    raise ValueError("Desktop project version is missing")
version = version.strip()
root, output = args.stage.resolve(), args.output.resolve()
if not root.is_relative_to(project / "release") or not output.is_relative_to(project / "release"):
    raise ValueError("Release packaging must stay in the project's release directory")
auditor = project / "scripts/audit-release.py"
subprocess.run([sys.executable, "-I", "-B", str(auditor), "--stage", str(root)], check=True)
files = []
for path in sorted(root.rglob("*")):
    relative = path.relative_to(root)
    if relative.parts[0] in {"data", "cache"}:
        continue
    if path.is_symlink() or getattr(path.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError("Linked publish entries are not supported")
    if path.is_file():
        if path.suffix in {".part", ".partial", ".pyc"} or "__pycache__" in relative.parts:
            raise ValueError("Build/runtime cache in release: " + str(relative))
        files.append(path)
expected = {}
engine_prefix = "engines/gpt-sovits/"
manifest = json.loads((root / engine_prefix / "laidian-engine.json").read_text(encoding="utf-8"))
for item in manifest["files"]:
    expected[engine_prefix + item["path"]] = item["sha256"]
for path in files:
    name = path.relative_to(root).as_posix()
    if name not in expected:
        with path.open("rb") as stream:
            expected[name] = hashlib.file_digest(stream, "sha256").hexdigest()
temporary = output.with_suffix(output.suffix + ".part")
with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as archive:
    for index, path in enumerate(files, 1):
        archive.write(path, path.relative_to(root).as_posix())
        if index % 3000 == 0:
            print(f"Packed {index}/{len(files)}", flush=True)
with zipfile.ZipFile(temporary) as archive:
    if set(archive.namelist()) != set(expected):
        raise ValueError("Archive inventory mismatch")
    for index, info in enumerate(archive.infolist(), 1):
        digest = hashlib.sha256()
        with archive.open(info) as stream:
            for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
                digest.update(block)
        if digest.hexdigest() != expected[info.filename]:
            raise ValueError("Archive hash mismatch: " + info.filename)
        if index % 3000 == 0:
            print(f"Verified {index}/{len(files)}", flush=True)
subprocess.run([sys.executable, "-I", "-B", str(auditor), "--zip", str(temporary)], check=True)
temporary.replace(output)
with output.open("rb") as stream:
    digest = hashlib.file_digest(stream, "sha256").hexdigest()
record = dict(version=version, file=output.name, bytes=output.stat().st_size,
              sha256=digest, fileCount=len(files), excludes=["data", "cache"])
output.with_suffix(".manifest.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
print(json.dumps(record), flush=True)
