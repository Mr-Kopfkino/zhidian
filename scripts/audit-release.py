"""Fail closed on release inventories, runtime traces and local credentials.

This is a packaging gate, not a replacement for archive SHA-256 verification.
Public third-party source fixtures are permitted only through the engine manifest.
No user state, credentials or character files are opened by this audit.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
RELEASE = PROJECT / "release"
ENGINE = "engines/gpt-sovits/"
ENGINE_MANIFEST = ENGINE + "laidian-engine.json"
APP_FILES = {
    "Zhidian.exe", "Zhidian.dll", "Zhidian.deps.json", "Zhidian.runtimeconfig.json",
    "Qiban.Core.dll", "README.md", "Upgrade-Data.ps1", "VoiceEngine/gpt_sovits_worker.py",
    "docs/KOTORI.md", "docs/VOICE_ENGINE.md", "docs/THEME.md", "licenses/XP.css-LICENSE.txt",
    "LICENSE", "THIRD_PARTY_NOTICES.md",
}
LICENSES = {
    "licenses/microsoft.netcore.app.runtime.win-x64-LICENSE.TXT.txt",
    "licenses/microsoft.netcore.app.runtime.win-x64-THIRD-PARTY-NOTICES.TXT.txt",
    "licenses/microsoft.windowsdesktop.app.runtime.win-x64-LICENSE.txt",
}
SATELLITES = {
    "PresentationCore", "PresentationFramework", "PresentationUI", "ReachFramework",
    "System.Windows.Controls.Ribbon", "System.Windows.Input.Manipulations", "System.Xaml",
    "UIAutomationClient", "UIAutomationClientSideProviders", "UIAutomationProvider",
    "UIAutomationTypes", "WindowsBase",
}
FORBIDDEN_PARTS = {"__pycache__", ".git", ".cache", "cache", "logs", "log", "tmp", "temp", ".aws", ".ssh"}
FORBIDDEN_NAMES = {"state.json", "state.backup.json", "startup-check.txt", ".env", "credentials", "credentials.json", "token.json"}
FORBIDDEN_SUFFIXES = {".pyc", ".pyo", ".log", ".tmp", ".part", ".partial", ".sqlite", ".sqlite3", ".db", ".bak", ".dmp"}
TEXT_SUFFIXES = {".py", ".pyi", ".ps1", ".bat", ".cmd", ".txt", ".md", ".rst", ".json", ".yaml", ".yml", ".ini", ".cfg", ".toml", ".xml", ".lock", ".pth", ".h", ".hpp", ".c", ".cpp"}
TEXT_NAMES = {"metadata", "installer", "record", "license", "copying", "notice", "authors", "wheel"}
SECRET_PATTERNS = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----"),
    "API token": re.compile(r"\b(?:sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}|hf_[A-Za-z0-9]{30,}|gh[pousr]_[A-Za-z0-9]{30,}|AKIA[A-Z0-9]{16})\b"),
    "credential-bearing URL": re.compile(r"https?://[^\s/:@\"'<>]+:[^\s/@\"'<>]+@"),
}
# Reviewed upstream examples, pinned to their exact file bytes. These are not
# user credentials: Transformers' public sandbox-CI token and URL-parser docs.
# Any edits invalidate the exception and still fail the engine hash check.
UPSTREAM_EXAMPLES = {
    (ENGINE + "runtime/Lib/site-packages/transformers/testing_utils.py", "API token"): "ada7ad740e285e641597661b398fe469410dbd3299a99cca6371bde65ca5a085",
    (ENGINE + "runtime/Lib/site-packages/future/backports/urllib/request.py", "credential-bearing URL"): "691f593337e15750b6424df016c1af930c6ab5d3d376c255191b790d40b0809f",
    (ENGINE + "runtime/Lib/site-packages/pydantic/networks.py", "credential-bearing URL"): "6085ec09a594a84ec533000ee6e8d85c294c2f123728fe9ed8c755c273f95f8b",
    (ENGINE + "runtime/Lib/site-packages/urllib3/util/url.py", "credential-bearing URL"): "6a2814d91987dd9728278d413a154ee5277b15cb262d9945425377f2a693311f",
}


def safe_name(name):
    path = PurePosixPath(name)
    if not name or path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name or str(path) != name or any(ord(c) < 32 for c in name):
        raise ValueError("Unsafe archive or manifest path")
    return path


def checked_release_path(path):
    path = path.absolute()
    for parent in (path, *path.parents):
        if parent.exists() and getattr(parent.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError("Reparse point in release path")
    resolved = path.resolve()
    if not resolved.is_relative_to(RELEASE):
        raise ValueError("Audit input must stay inside project release directory")
    return resolved


class Source:
    def __init__(self, path, zipped):
        self.path = checked_release_path(path)
        self.zip = zipfile.ZipFile(self.path) if zipped else None
        self.entries = {}
        self.directories = set()
        if self.zip:
            if self.zip.comment:
                raise ValueError("Archive comment is not permitted")
            for info in self.zip.infolist():
                name = info.filename.rstrip("/") if info.is_dir() else info.filename
                safe_name(name)
                if info.comment or stat.S_ISLNK(info.external_attr >> 16):
                    raise ValueError("Archive comments or symlinks are not permitted")
                if info.is_dir():
                    self.directories.add(name)
                else:
                    self.add(name, info.file_size)
        else:
            # Do not traverse data/cache, even if an accidental user directory is present.
            for current, dirs, files in os.walk(self.path, followlinks=False):
                for name in list(dirs):
                    child = Path(current) / name
                    relative = child.relative_to(self.path).as_posix()
                    self.directories.add(relative)
                    if child.is_symlink() or getattr(child.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                        raise ValueError("Reparse point in staged release")
                    if relative.split("/")[0] in {"data", "cache"}:
                        dirs.remove(name)
                for name in files:
                    child = Path(current) / name
                    if child.is_symlink() or getattr(child.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                        raise ValueError("Reparse point in staged release")
                    self.add(child.relative_to(self.path).as_posix(), child.stat().st_size)

    def add(self, name, size):
        safe_name(name)
        if name.casefold() in self.entries:
            raise ValueError("Duplicate case-insensitive release entry")
        self.entries[name.casefold()] = (name, size)

    def read(self, name):
        if name.split("/")[0] in {"data", "cache"}:
            raise ValueError("User state and cache are never read by release audits")
        return self.zip.read(name) if self.zip else (self.path / name).read_bytes()


def audit(source):
    errors = []
    names = {item[0] for item in source.entries.values()}
    for name in names | source.directories:
        path = safe_name(name)
        folded = {p.casefold() for p in path.parts}
        if path.parts[0].casefold() in {"data", "cache"} or folded & FORBIDDEN_PARTS or path.name.casefold() in FORBIDDEN_NAMES or path.suffix.casefold() in FORBIDDEN_SUFFIXES:
            errors.append("Runtime/private entry: " + name)
    if not APP_FILES <= names or ENGINE_MANIFEST not in names:
        raise ValueError("Required application or engine files are missing")
    deps = json.loads(source.read("Zhidian.deps.json"))
    target = deps["targets"][deps["runtimeTarget"]["name"]]
    allowed_app = set(APP_FILES)
    portable = any(k.startswith("runtimepack.") for k in target)
    for package, assets in target.items():
        if not re.fullmatch(r"(?:Zhidian|Qiban\.Core|runtimepack\.Microsoft\.(?:NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/[A-Za-z0-9.+-]+", package):
            errors.append("Unexpected application dependency")
        for kind in ("native", "runtime"):
            for name in assets.get(kind, {}):
                path = safe_name(name)
                if path.suffix.casefold() not in {".dll", ".exe"}:
                    errors.append("Unexpected runtime asset type")
                allowed_app.add(path.name)
    if portable:
        allowed_app |= LICENSES | {f"zh-Hans/{name}.resources.dll" for name in SATELLITES}
        if not LICENSES <= names or "coreclr.dll" not in names:
            errors.append("Portable runtime or license inventory incomplete")
    engine = json.loads(source.read(ENGINE_MANIFEST))
    files = engine["files"]
    expected_engine = {ENGINE_MANIFEST}
    hashes = {}
    for entry in files:
        safe_name(entry["path"])
        name = ENGINE + entry["path"]
        if name.casefold() in hashes:
            errors.append("Duplicate engine manifest entry")
        hashes[name.casefold()] = entry["sha256"]
        expected_engine.add(name)
        actual = source.entries.get(name.casefold())
        if actual and actual[1] != entry["size"]:
            errors.append("Engine size mismatch: " + name)
    if len(files) != engine["fileCount"] or len(expected_engine) != len(files) + 1:
        errors.append("Engine manifest count mismatch")
    actual_engine = {name for name in names if name.startswith(ENGINE)}
    if actual_engine != expected_engine:
        errors.extend("Unlisted/missing engine entry: " + name for name in sorted(actual_engine ^ expected_engine))
    errors.extend("Unlisted application entry: " + name for name in sorted(names - expected_engine - allowed_app))
    expected_directories = {str(parent) for name in expected_engine | allowed_app for parent in PurePosixPath(name).parents if str(parent) != "."}
    errors.extend("Unlisted directory: " + name for name in sorted(source.directories - expected_directories))

    resources = json.loads(source.read(ENGINE + "resource-manifest.json"))
    public_resources = {}
    for resource in resources:
        safe_name(resource["path"])
        name = ENGINE + resource["path"]
        if not resource["url"].startswith(("https://huggingface.co/", "https://raw.githubusercontent.com/", "https://github.com/", "https://dl.fbaipublicfiles.com/fasttext/")):
            errors.append("Unexpected public resource origin: " + name)
        if hashes.get(name.casefold()) != resource["sha256"]:
            errors.append("Public resource manifest hash mismatch: " + name)
        public_resources[name] = resource
    audio_fixtures = 0
    model_weights = 0
    for name in actual_engine:
        suffix = PurePosixPath(name).suffix.casefold()
        if name.startswith(ENGINE + "GPT_SoVITS/pretrained_models/") and suffix in {".pth", ".pt", ".ckpt", ".bin", ".safetensors"}:
            model_weights += 1
            if name not in public_resources:
                errors.append("Model lacks public resource provenance: " + name)
        if suffix in {".wav", ".mp3", ".flac", ".ogg", ".m4a"}:
            if name.startswith(ENGINE + "runtime/Lib/site-packages/scipy/io/tests/data/test-") and suffix == ".wav":
                audio_fixtures += 1
            else:
                errors.append("Unexpected audio asset: " + name)

    # Do not print matched strings; reports contain only rule and packaged relative filename.
    local_paths = [str(PROJECT), str(Path(os.environ.get("USERPROFILE", str(Path.home()))))]
    local_paths = [p.casefold().replace("\\", "/") for p in local_paths if len(p) > 5]
    scanned_text = 0
    upstream_examples = set()
    for name, size in source.entries.values():
        path = PurePosixPath(name)
        if path.parts[0] in {"data", "cache"}:
            continue
        is_text = path.suffix.casefold() in TEXT_SUFFIXES or path.name.casefold() in TEXT_NAMES
        own_binary = name in {"Zhidian.exe", "Zhidian.dll", "Qiban.Core.dll"}
        if not (is_text or own_binary) or size > 16 * 1024 * 1024:
            continue
        raw = source.read(name)
        digest = hashlib.sha256(raw).hexdigest()
        if is_text:
            if name.startswith(ENGINE) and digest != hashes.get(name.casefold(), digest):
                errors.append("Engine text hash mismatch: " + name)
            scanned_text += 1
        texts = [raw.decode("utf-8", errors="replace")]
        if own_binary or raw.startswith((b"\xff\xfe", b"\xfe\xff")):
            texts.append(raw.decode("utf-16-le", errors="ignore"))
        for text in texts:
            normalized = text.casefold().replace("\\\\", "\\").replace("\\", "/")
            if any(local in normalized for local in local_paths):
                errors.append("Local machine path: " + name)
            if is_text:
                for label, pattern in SECRET_PATTERNS.items():
                    if pattern.search(text):
                        if UPSTREAM_EXAMPLES.get((name, label)) == digest:
                            upstream_examples.add(name)
                        else:
                            errors.append(label + ": " + name)
    return {
        "file": source.path.name,
        "passed": not errors,
        "fileCount": len(names),
        "engineFileCount": len(actual_engine),
        "textFilesScanned": scanned_text,
        "publicBaseModelFiles": model_weights,
        "upstreamScipyAudioFixtures": audio_fixtures,
        "verifiedUpstreamExampleFiles": len(upstream_examples),
        "userDataRead": False,
        "errors": sorted(set(errors)),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source_arg = parser.add_mutually_exclusive_group(required=True)
    source_arg.add_argument("--stage", type=Path)
    source_arg.add_argument("--zip", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = audit(Source(args.zip or args.stage, bool(args.zip)))
    content = json.dumps(result, ensure_ascii=False, indent=2)
    if args.report:
        report = args.report.resolve()
        if not report.is_relative_to(PROJECT / "docs/test-results"):
            raise ValueError("Audit report must be under docs/test-results")
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(content, encoding="utf-8")
    print(content)
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
