"""Extract the five required x64 VC runtime DLLs without installing the redist.

Only pinned Microsoft EXE/CAB bytes are parsed. No MSI or installer is executed;
expand.exe decompresses cabinets inside the project build directory. Replacement
uses a new file and os.replace so existing release hardlinks remain unchanged.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET

PROJECT = Path(__file__).resolve().parents[1]
VERSION = "14.51.36247.0"
INSTALLER_SHA = "843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c"
INSTALLER_URL = "https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe"
LICENSE_URL = "https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx"
LICENSE_SHA = "08651651a7602fc7c0e2763de0fde1ff9f868df2780597cd1775ee9d6441c783"
DLLS = ("msvcp140.dll", "msvcp140_1.dll", "vcomp140.dll", "vcruntime140.dll", "vcruntime140_1.dll")


def checked(path: Path) -> Path:
    path = path.absolute()
    for part in (path, *path.parents):
        if part.exists() and (part.is_symlink() or getattr(part.stat(), "st_file_attributes", 0) & 0x400):
            raise ValueError("Linked runtime/build paths are not supported")
    path = path.resolve()
    if path == PROJECT or not path.is_relative_to(PROJECT):
        raise ValueError("Runtime preparation must remain inside this project")
    return path


def sha(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def atomic_copy(source: Path, destination: Path) -> None:
    destination = checked(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=".vc-runtime-", suffix=".tmp", dir=destination.parent)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "wb") as output, source.open("rb") as incoming:
            shutil.copyfileobj(incoming, output)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def download(url: str, target: Path, expected: str) -> Path:
    target = checked(target)
    if target.is_file() and sha(target) == expected:
        return target
    temporary = checked(target.with_name(target.name + ".part"))
    try:
        with urllib.request.urlopen(url, timeout=120) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output)
        if sha(temporary) != expected:
            raise ValueError("Microsoft source hash does not match the pinned build")
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)
    return target


def expand(cabinet: Path, destination: Path) -> None:
    destination.mkdir()
    executable = Path(os.environ["SystemRoot"]) / "System32/expand.exe"
    subprocess.run([str(executable), "-F:*", str(cabinet), str(destination)], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                   creationflags=subprocess.CREATE_NO_WINDOW)


def extract(installer: Path, work: Path) -> Path:
    data = installer.read_bytes()
    start, cabinets = 0, []
    while (start := data.find(b"MSCF", start)) >= 0:
        try:
            _, r1, size, r2, files, r3, minor, major, *_ = struct.unpack_from("<4sIIIIIBBHHHHH", data, start)
            if r1 == r2 == r3 == 0 and minor == 3 and major == 1 and 36 <= files < size and start + size <= len(data):
                cabinet = work / f"bundle-{len(cabinets)}.cab"
                cabinet.write_bytes(data[start:start + size])
                cabinets.append(cabinet)
        except struct.error:
            pass
        start += 4
    if len(cabinets) != 2:
        raise ValueError("Unexpected pinned Microsoft bundle layout")
    ux, payloads = work / "ux", work / "payloads"
    expand(cabinets[0], ux)
    expand(cabinets[1], payloads)
    manifest = ET.parse(ux / "0").getroot()
    namespace = {"b": "http://schemas.microsoft.com/wix/2008/Burn"}
    if manifest.find("b:Registration", namespace).get("Version") != VERSION:
        raise ValueError("Unexpected VC runtime version")
    cabinet_payload = next(p for p in manifest.findall("b:Payload", namespace)
                           if p.get("FilePath") == r"packages\vcRuntimeMinimum_amd64\cab1.cab")
    source_name = cabinet_payload.get("SourcePath", "")
    if Path(source_name).name != source_name:
        raise ValueError("Invalid cabinet payload")
    dlls = work / "dlls"
    expand(payloads / source_name, dlls)
    for name in DLLS:
        data = (dlls / (name + "_amd64")).read_bytes()
        pe = struct.unpack_from("<I", data, 0x3c)[0]
        if data[:2] != b"MZ" or data[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", data, pe + 4)[0] != 0x8664:
            raise ValueError("Expected an x64 runtime DLL")
    return dlls


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", required=True, type=Path)
    parser.add_argument("--work-root", type=Path, default=PROJECT / ".build/voice-distribution/vc-runtime")
    parser.add_argument("--stage-directory", type=Path, help="Write an engine-shaped staging tree; the calling PowerShell process publishes it after Python exits")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("VC runtime assembly requires Windows expand.exe")
    engine, cache = checked(args.engine_root), checked(args.work_root)
    if not (engine / "runtime/python.exe").is_file():
        parser.error("Prepare the embedded Python runtime first")
    destination = checked(args.stage_directory) if args.stage_directory else engine
    runtime = checked(destination / "runtime")
    if Path(sys.executable).resolve().parent == runtime:
        parser.error("This Python process is using the destination DLLs; use --stage-directory, then publish after it exits, or run an independent Python")
    cache.mkdir(parents=True, exist_ok=True)
    environment = cache / "environment"
    environment.mkdir(exist_ok=True)
    for variable in ("TEMP", "TMP", "TMPDIR", "APPDATA", "LOCALAPPDATA", "HOME", "USERPROFILE"):
        os.environ[variable] = str(environment)
    tempfile.tempdir = str(environment)
    installer = download(INSTALLER_URL, cache / "vc_redist.x64.exe", INSTALLER_SHA)
    license_file = download(LICENSE_URL, cache / "Microsoft-VC-Runtime-License.docx", LICENSE_SHA)
    with tempfile.TemporaryDirectory(prefix="extract-", dir=cache) as temporary:
        dlls = extract(installer, Path(temporary))
        records = []
        for name in DLLS:
            source = dlls / (name + "_amd64")
            records.append({"path": "runtime/" + name, "size": source.stat().st_size, "sha256": sha(source)})
        for name in DLLS:
            atomic_copy(dlls / (name + "_amd64"), runtime / name)
        atomic_copy(license_file, destination / "licenses/Microsoft-VC-Runtime-License.docx")
        report = {"version": VERSION, "architecture": "x64", "deployment": "application-local",
                  "source": INSTALLER_URL, "sourceSha256": INSTALLER_SHA,
                  "license": LICENSE_URL, "licenseSha256": LICENSE_SHA, "files": records}
        report_file = Path(temporary) / "vc-runtime-manifest.json"
        report_file.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        atomic_copy(report_file, destination / "vc-runtime-manifest.json")
    print(f"Prepared VC runtime {VERSION}: {len(DLLS)} x64 DLLs, {sum(item['size'] for item in records)} bytes; no system installation.")


if __name__ == "__main__":
    main()
