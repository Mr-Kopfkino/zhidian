"""Update an existing project release while holding its application mutex.

User data/cache are never copied, replaced or removed. Engine files use same-volume
hardlinks; regular application files are replaced from a verified staging folder.
"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import os
from pathlib import Path
import re
import shutil
import stat
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("--name", choices=("Laidian-Light", "Laidian-Portable"), required=True)
parser.add_argument("--app-only", action="store_true", help="Reuse the existing target voice engine")
args = parser.parse_args()
project = Path(__file__).resolve().parents[1]
version = ET.parse(project / "src/Qiban.Desktop/Qiban.Desktop.csproj").findtext(".//Version", "").strip()
if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9.-]+)?", version):
    raise ValueError("Desktop project version is missing or invalid")
stage = project / "release/.staging" / args.name
target = project / "release" / args.name


def real_path(path):
    for entry in (path, *path.parents):
        if entry.exists() and getattr(entry.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError("Release paths cannot contain reparse points")
    return path.resolve()


stage, target = real_path(stage), real_path(target)
if not stage.is_relative_to(project / "release/.staging") or target.parent != project / "release":
    raise ValueError("Invalid release boundary")
engine_root = target if args.app_only else stage
if not (engine_root / "engines/gpt-sovits/laidian-engine.json").is_file() or not (stage / "Zhidian.exe").is_file():
    raise ValueError("Incomplete staged release")
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
kernel.CreateMutexW.restype = wintypes.HANDLE
kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
mutex_id = hashlib.sha256((str(target) + os.sep).upper().encode("utf-8")).hexdigest().upper()[:20]
handle = kernel.CreateMutexW(None, True, "Local\\Qiban-" + mutex_id)
if not handle:
    raise ctypes.WinError(ctypes.get_last_error())
if ctypes.get_last_error() == 183:
    kernel.CloseHandle(handle)
    raise RuntimeError("Close the application before updating this release")
try:
    snapshot = project / ".build/update-check" / version / args.name
    snapshot.mkdir(parents=True, exist_ok=True)
    state = target / "data/state.json"
    original_state = state.read_bytes() if state.exists() else None
    if state.exists():
        before = snapshot / "before.json"
        if not before.exists():
            shutil.copyfile(state, before)
    files = []
    for path in stage.rglob("*"):
        relative = path.relative_to(stage)
        if relative.parts[0] in {"data", "cache"} or (args.app_only and relative.parts[0] == "engines"):
            continue
        real_path(path)
        if path.is_file():
            files.append((path, relative))
    for source, relative in files:
        destination = real_path(target / relative)
        if not destination.is_relative_to(target):
            raise ValueError("Update escaped release directory")
        destination.parent.mkdir(parents=True, exist_ok=True)
        if relative.parts[0] == "engines":
            if destination.exists() and os.path.samefile(source, destination):
                continue
            destination.unlink(missing_ok=True)
            os.link(source, destination)
        else:
            temporary = destination.with_name(destination.name + ".update")
            shutil.copyfile(source, temporary)
            temporary.replace(destination)
    for source, relative in files:
        destination = target / relative
        if relative.parts[0] == "engines":
            if not os.path.samefile(source, destination):
                raise ValueError("Engine file was not linked correctly")
        else:
            with source.open("rb") as a, destination.open("rb") as b:
                if hashlib.file_digest(a, "sha256").digest() != hashlib.file_digest(b, "sha256").digest():
                    raise ValueError("Application file verification failed")
    if state.exists() and state.read_bytes() != original_state:
        raise ValueError("User state changed during program update")
    for name in ("Laidian.exe", "Laidian.dll", "Laidian.deps.json", "Laidian.runtimeconfig.json"):
        obsolete = real_path(target / name)
        if obsolete.parent != target:
            raise ValueError("Obsolete application path escaped release directory")
        obsolete.unlink(missing_ok=True)
    print(f"Updated {args.name}: {len(files)} verified program files; user state unchanged", flush=True)
finally:
    kernel.ReleaseMutex(handle)
    kernel.CloseHandle(handle)
