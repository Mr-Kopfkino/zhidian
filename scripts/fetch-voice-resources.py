"""Fetch inference-only resources, verify upstream LFS hashes, record provenance.

No Hugging Face client/cache, no training weights, no WebUI bundle. Interrupted
downloads remain as .part files inside the selected engine and are overwritten.
"""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import time
import urllib.parse
import urllib.request


def read_json(url):
    with urllib.request.urlopen(url, timeout=90) as response:
        return json.load(response)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine-root", type=Path, required=True)
    parser.add_argument("--common-only", action="store_true")
    parser.add_argument("--resource-lock", type=Path, default=Path(__file__).with_name("voice-resources.lock.json"))
    args = parser.parse_args()
    root = args.engine_root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    base = "GPT_SoVITS/pretrained_models/"
    selected = [
        "chinese-hubert-base/config.json", "chinese-hubert-base/preprocessor_config.json",
        "chinese-hubert-base/pytorch_model.bin", "chinese-roberta-wwm-ext-large/config.json",
        "chinese-roberta-wwm-ext-large/tokenizer.json", "chinese-roberta-wwm-ext-large/pytorch_model.bin",
        "sv/pretrained_eres2netv2w24s4ep4.ckpt",
    ]
    if not args.common_only:
        selected += ["s2Gv3.pth", "gsv-v4-pretrained/s2Gv4.pth",
                     "gsv-v5-pretrained/s2Gv5dev.pth", "gsv-v5-pretrained/s2Gv5turbo.pth",
                     "gsv-v4-pretrained/vocoder.pth", "gsv-v5-pretrained/vocoder.pth",
                     "models--nvidia--bigvgan_v2_24khz_100band_256x/config.json",
                     "models--nvidia--bigvgan_v2_24khz_100band_256x/bigvgan_generator.pt"]
    resources = []
    def add_repo(repo, paths, prefix):
        revision = read_json("https://huggingface.co/api/models/" + repo)["sha"]
        entries = read_json(f"https://huggingface.co/api/models/{repo}/tree/{revision}?recursive=true")
        by_path = {item["path"]: item for item in entries}
        for name in paths:
            item = by_path[name]
            resources.append(dict(path=prefix + name, size=item["size"],
                sha256=item.get("lfs", {}).get("oid"),
                url=f"https://huggingface.co/{repo}/resolve/{revision}/" + urllib.parse.quote(name)))
    if args.resource_lock.is_file():
        resources = json.loads(args.resource_lock.read_text(encoding="utf-8-sig"))
        if args.common_only:
            allowed = {base + name for name in selected}
            resources = [item for item in resources if item["path"] in allowed or item["path"].endswith(("lid.176.bin", "ffmpeg.exe", "vocab.txt", "tokenizer_config.json", "special_tokens_map.json"))]
    else:
        add_repo("lj1995/GPT-SoVITS", selected, base)
        add_repo("hfl/chinese-roberta-wwm-ext-large",
                 ["tokenizer_config.json", "special_tokens_map.json", "vocab.txt"], base + "chinese-roberta-wwm-ext-large/")
        # The official project links this single executable; ffprobe is unused by TTS.
        add_repo("lj1995/VoiceConversionWebUI", ["ffmpeg.exe"], "runtime/")
        resources.append(dict(path=base + "fast_langdetect/lid.176.bin", size=131266198,
            sha256=None, url="https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.bin"))

    def fetch(item):
        target = (root / item["path"]).resolve()
        if not target.is_relative_to(root) or target == root:
            raise ValueError("Resource path escaped engine")
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.is_file() and target.stat().st_size == item["size"]:
            digest = sha256(target)
            if not item["sha256"] or digest == item["sha256"]:
                item["sha256"] = digest
                print("Verified " + item["path"], flush=True)
                return
        part = target.with_name(target.name + ".part")
        for attempt in range(4):
            try:
                digest = hashlib.sha256()
                with urllib.request.urlopen(item["url"], timeout=120) as response, part.open("wb") as output:
                    for block in iter(lambda: response.read(2 * 1024 * 1024), b""):
                        output.write(block)
                        digest.update(block)
                if part.stat().st_size != item["size"]:
                    raise ValueError("Resource size mismatch: " + item["path"])
                if item["sha256"] and digest.hexdigest() != item["sha256"]:
                    raise ValueError("Resource hash mismatch: " + item["path"])
                item["sha256"] = digest.hexdigest()
                part.replace(target)
                print("Downloaded " + item["path"], flush=True)
                return
            except Exception:
                part.unlink(missing_ok=True)
                if attempt == 3:
                    raise
                time.sleep(2 + attempt)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(fetch, resources))
    (root / "resource-manifest.json").write_text(json.dumps(resources, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
