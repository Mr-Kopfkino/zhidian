"""Keep license texts and source provenance next to bundled inference binaries."""
import argparse
import json
from pathlib import Path
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--engine-root", type=Path, required=True)
args = parser.parse_args()
root = args.engine_root.resolve()
licenses = root / "licenses"
licenses.mkdir(parents=True, exist_ok=True)
documents = {
    "FFmpeg-LGPL-3.0.txt": "https://raw.githubusercontent.com/FFmpeg/FFmpeg/fbb9368226/COPYING.LGPLv3",
    "FFmpeg-GPL-3.0.txt": "https://raw.githubusercontent.com/FFmpeg/FFmpeg/fbb9368226/COPYING.GPLv3",
    "fastText-LICENSE.txt": "https://raw.githubusercontent.com/facebookresearch/fastText/main/LICENSE",
    "BERT-Apache-2.0.txt": "https://raw.githubusercontent.com/google-research/bert/master/LICENSE",
}
for name, url in documents.items():
    target = licenses / name
    if not target.exists():
        with urllib.request.urlopen(url, timeout=60) as response:
            target.write_bytes(response.read())
(licenses / "SOURCES.txt").write_text("""GPT-SoVITS inference source: https://github.com/RVC-Boss/GPT-SoVITS/tree/04a44eb977586645f33594b72af13a035a2056c6
Local modifications are recorded in laidian-source-trim.json and laidian-cache-patches.json.
Python: https://www.python.org/downloads/release/python-3119/ (runtime/LICENSE.txt)
PyTorch: https://github.com/pytorch/pytorch/tree/v2.6.0 (torch dist-info licenses retained)
TorchAudio: https://github.com/pytorch/audio/tree/v2.6.0
FFmpeg binary: official GPT-SoVITS README link to lj1995/VoiceConversionWebUI/ffmpeg.exe
FFmpeg source: https://github.com/FFmpeg/FFmpeg/tree/fbb9368226
FFmpeg is a separate, unmodified LGPLv3 executable; ffmpeg.exe -buildconf displays its build configuration.
fastText language identification: https://fasttext.cc/docs/en/language-identification.html
Language identification model data: CC-BY-SA-3.0; https://creativecommons.org/licenses/by-sa/3.0/
HuBERT: https://huggingface.co/TencentGameMate/chinese-hubert-base
Chinese BERT: https://huggingface.co/hfl/chinese-roberta-wwm-ext-large
GPT-SoVITS half precision models and vocoders: https://huggingface.co/lj1995/GPT-SoVITS
NLTK dictionaries: https://github.com/nltk/nltk_data (resource LICENSE/README files retained)
Python dependency licenses remain in runtime/Lib/site-packages/*dist-info and package license files.
Resource URLs, revisions and SHA-256 are recorded in resource-manifest.json.
No user-trained character weights or reference audio are distributed.
""", encoding="utf-8")
print("Voice component notices prepared")
