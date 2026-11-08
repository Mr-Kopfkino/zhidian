"""Prepare small NLTK resources offline; never use the user's NLTK directory."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("--engine-root", required=True, type=Path)
args = parser.parse_args()
root = args.engine_root.resolve() / "runtime/nltk_data"
records = []
lock_path = Path(__file__).with_name("voice-dictionaries.lock.json")
locked = {item["path"]: item for item in json.loads(lock_path.read_text(encoding="utf-8-sig"))} if lock_path.exists() else {}
for category, name in [("corpora", "cmudict"), ("taggers", "averaged_perceptron_tagger"),
                       ("taggers", "averaged_perceptron_tagger_eng")]:
    target = root / category / (name + ".zip")
    target.parent.mkdir(parents=True, exist_ok=True)
    url = f"https://raw.githubusercontent.com/nltk/nltk_data/gh-pages/packages/{category}/{name}.zip"
    if not target.exists():
        with urllib.request.urlopen(url, timeout=90) as response:
            data = response.read()
        target.write_bytes(data)
    expected = locked.get(category + "/" + name + ".zip", {}).get("sha256")
    if expected and hashlib.sha256(target.read_bytes()).hexdigest() != expected:
        raise ValueError("Dictionary hash mismatch: " + name)
    with zipfile.ZipFile(target) as archive:
        for info in archive.infolist():
            destination = (target.parent / info.filename).resolve()
            if not destination.is_relative_to(target.parent.resolve()):
                raise ValueError("Dictionary archive path escaped destination")
        archive.extractall(target.parent)
    records.append(dict(path=target.relative_to(root).as_posix(), url=url,
                        sha256=hashlib.sha256(target.read_bytes()).hexdigest()))
    # Keep cmudict/legacy tagger ZIP: g2p_en checks these exact resources at import.
    if name.endswith("_eng"):
        target.unlink()
(root / "resources.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
print("Offline NLTK dictionaries prepared")
