"""Exercise release privacy gates in an isolated synthetic project under .build."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

workspace = Path(__file__).resolve().parents[2]
fixture_parent = workspace / ".build/release-audit"
fixture_parent.mkdir(parents=True, exist_ok=True)
checks = 0


def check(condition):
    global checks
    assert condition
    checks += 1


with tempfile.TemporaryDirectory(prefix="fixture-", dir=fixture_parent) as folder:
    project = Path(folder).resolve()
    if not project.is_relative_to(fixture_parent.resolve()):
        raise ValueError("Test fixture escaped the project build directory")
    scripts = project / "scripts"
    scripts.mkdir()
    for name in ("audit-release.py", "package-release.py"):
        shutil.copyfile(workspace / "scripts" / name, scripts / name)
    csproj = project / "src/Qiban.Desktop/Qiban.Desktop.csproj"
    csproj.parent.mkdir(parents=True)
    csproj.write_text("<Project><PropertyGroup><Version>2.1.0</Version></PropertyGroup></Project>", encoding="utf-8")
    spec = importlib.util.spec_from_file_location("release_audit", scripts / "audit-release.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    root = project / "release"
    stage = root / "clean"
    stage.mkdir(parents=True)
    for name in module.APP_FILES:
        target = stage / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text("public application fixture", encoding="utf-8")
    (stage / "Zhidian.deps.json").write_text(json.dumps({"runtimeTarget": {"name": "test"}, "targets": {"test": {"Zhidian/2.1.0": {"runtime": {"Zhidian.dll": {}}}, "Qiban.Core/1.0.0": {"runtime": {"Qiban.Core.dll": {}}}}}}), encoding="utf-8")
    resource = stage / module.ENGINE / "resource-manifest.json"
    resource.parent.mkdir(parents=True)
    resource.write_text("[]", encoding="utf-8")
    (stage / module.ENGINE_MANIFEST).write_text(json.dumps({"fileCount": 1, "files": [{"path": "resource-manifest.json", "size": 2, "sha256": hashlib.sha256(b"[]").hexdigest()}]}), encoding="utf-8")
    check(module.audit(module.Source(stage, False))["passed"])
    data = stage / "data"
    data.mkdir()
    (data / "state.json").write_text("PRIVATE DATA MUST NOT BE READ", encoding="utf-8")
    source = module.Source(stage, False)
    check("data/state.json" not in source.entries)
    check(not module.audit(source)["passed"])
    (data / "state.json").unlink()
    data.rmdir()
    rogue = stage / "personal.json"
    rogue.write_text("{}", encoding="utf-8")
    check(not module.audit(module.Source(stage, False))["passed"])
    rogue.unlink()
    rogue = resource.parent / "personal.wav"
    rogue.write_bytes(b"RIFF")
    check(not module.audit(module.Source(stage, False))["passed"])
    rogue.unlink()
    readme = stage / "README.md"
    readme.write_text("sk-" + "a" * 40, encoding="utf-8")
    check(any("API token" in e for e in module.audit(module.Source(stage, False))["errors"]))
    readme.write_text(str(project), encoding="utf-8")
    check(any("Local machine path" in e for e in module.audit(module.Source(stage, False))["errors"]))
    readme.write_text("public fixture", encoding="utf-8")
    archive = root / "clean.zip.part"
    with zipfile.ZipFile(archive, "w") as zipped:
        for entry in stage.rglob("*"):
            if entry.is_file():
                zipped.write(entry, entry.relative_to(stage).as_posix())
    source = module.Source(archive, True)
    check(module.audit(source)["passed"])
    source.zip.close()
    with zipfile.ZipFile(archive, "a") as zipped:
        zipped.writestr("data/state.json", "private fixture")
    source = module.Source(archive, True)
    check(not module.audit(source)["passed"])
    source.zip.close()
    with zipfile.ZipFile(archive, "w") as zipped:
        zipped.writestr("../escape", "fixture")
    try:
        module.Source(archive, True)
        check(False)
    except ValueError:
        check(True)
    packaged = root / "packaged.zip"
    command = [sys.executable, "-I", "-B", str(scripts / "package-release.py"), "--stage", str(stage), "--output", str(packaged)]
    result = subprocess.run(command, capture_output=True, text=True)
    check(result.returncode == 0)
    initial = hashlib.sha256(packaged.read_bytes()).hexdigest()
    data.mkdir()
    (data / "state.json").write_text("private fixture", encoding="utf-8")
    result = subprocess.run(command, capture_output=True, text=True)
    check(result.returncode != 0)
    check(hashlib.sha256(packaged.read_bytes()).hexdigest() == initial)
    check(not packaged.with_suffix(".zip.part").exists())
print(f"PASS: {checks} release audit behavior checks")
