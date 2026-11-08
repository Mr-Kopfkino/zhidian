"""Real stdio-worker tests with a tiny fake TTS backend; no model downloads."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap
import unittest
import wave

PROJECT = Path(__file__).resolve().parents[2]
WORKER = PROJECT / "src/Qiban.Desktop/VoiceEngine/gpt_sovits_worker.py"

FAKE_TTS = '''
import json, os
from pathlib import Path
import numpy as np
class TTS_Config:
    default_configs = {"v2": {}}
    def __init__(self, value):
        self.custom = value["custom"]
        self.configs_path = "must-not-write-engine.yaml"
class TTS:
    def __init__(self, config):
        print("PRIVATE library diagnostic", flush=True)
        os.write(1, b"PRIVATE native diagnostic\\n")
        os.write(2, b"PRIVATE stderr diagnostic\\n")
        self.config = config
        work = Path(config.configs_path).parent
        Path(config.configs_path).write_text(json.dumps(config.custom), encoding="utf-8")
        with (work / "loads.jsonl").open("a", encoding="utf-8") as output:
            output.write(json.dumps(config.custom) + "\\n")
    def run(self, inputs):
        work = Path(self.config.configs_path).parent
        (work / "request.json").write_text(json.dumps(inputs), encoding="utf-8")
        if inputs["text"] == "network":
            import socket
            socket.create_connection(("127.0.0.1", 9), timeout=.1)
        if inputs["text"] == "outside":
            Path("outside.txt").write_text("must never be created")
        if inputs["text"] == "null_device":
            with open(os.devnull, "w") as sink: sink.write("discarded")
        if inputs["text"] == "empty": return
        if inputs["text"] == "invalid":
            yield 32000, np.array([np.nan])
            return
        if inputs["text"] == "silent":
            yield 32000, np.zeros(9600, dtype=np.int16)
            return
        if inputs["text"] == "huge":
            yield 32000, np.zeros(17 * 1024 * 1024, dtype=np.int16)
            return
        yield 32000, np.array([0, 100, -100, 32000, -32000], dtype=np.int16)
        if inputs["text"] == "failure": raise RuntimeError("PRIVATE user contents")
        yield 32000, np.array([1, 2, 3], dtype=np.int16)
'''


class WorkerTest(unittest.TestCase):
    def setUp(self):
        base = PROJECT / ".build/worker-tests"
        base.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="session-", dir=base)
        self.root = Path(self.temporary.name)
        self.engine = self.root / "engine"
        self.work = self.root / "cache/session"
        modules = self.engine / "GPT_SoVITS/TTS_infer_pack"
        modules.mkdir(parents=True)
        (modules / "TTS.py").write_text(textwrap.dedent(FAKE_TTS), encoding="utf-8")
        (self.engine / "torch.py").write_text(
            "class cuda:\n @staticmethod\n def is_available(): return False\n @staticmethod\n def empty_cache(): pass\n", encoding="utf-8")
        (self.engine / "GPT_SoVITS/process_ckpt.py").write_text(
            "def get_sovits_version_from_path_fast(path): return ('v2','v2',False)\n", encoding="utf-8")
        for name in ("chinese-roberta-wwm-ext-large", "chinese-hubert-base"):
            directory = self.engine / "GPT_SoVITS/pretrained_models" / name
            directory.mkdir(parents=True)
            (directory / "config.json").write_text("{}")
        self.gpt, self.sovits, self.reference = (self.root / name for name in ("role.ckpt", "role.pth", "voice.wav"))
        for path in (self.gpt, self.sovits, self.reference): path.write_bytes(b"fixture")
        self.process = subprocess.Popen([sys.executable, "-B", "-u", str(WORKER), "--engine-root", str(self.engine),
                                         "--work-root", str(self.work)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8",
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(self.call({"method": "initialize"}), {"id": "ready", "ok": True})

    def tearDown(self):
        if self.process.poll() is None:
            self.process.stdin.close()
            try: self.process.wait(timeout=5)
            except subprocess.TimeoutExpired: self.process.kill(); self.process.wait(timeout=5)
        self.process.stdout.close()
        self.process.stderr.close()
        self.temporary.cleanup()

    def call(self, request):
        self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        self.assertTrue(line, "worker exited before a protocol response")
        return json.loads(line)

    def request(self, method="synthesize", **overrides):
        result = {"id": "fixture-request", "method": method, "gptWeights": str(self.gpt),
                  "sovitsWeights": str(self.sovits), "referenceAudio": str(self.reference),
                  "referenceText": "reference", "language": "zh", "text": "こんにちは。", "japanese": True,
                  "outputPath": str(self.work / "voice.wav"), "device": "auto"}
        result.update(overrides)
        return result

    def loads(self):
        return [json.loads(line) for line in (self.work / "loads.jsonl").read_text().splitlines()]

    def test_load_without_reference_and_reuse(self):
        self.assertTrue(self.call(self.request("load", referenceAudio=""))["ok"])
        self.assertTrue(self.call(self.request("load", referenceAudio=""))["ok"])
        self.assertEqual(len(self.loads()), 1)
        self.assertFalse((self.engine / "must-not-write-engine.yaml").exists())
        self.assertEqual(self.loads()[0]["device"], "cpu")
        self.assertFalse(self.loads()[0]["is_half"])

    def test_real_wav_japanese_and_private_output(self):
        response = self.call(self.request())
        self.assertTrue(response["ok"])
        self.assertEqual(response["outputPath"], str(self.work / "voice.wav"))
        with wave.open(response["outputPath"]) as audio:
            self.assertEqual((audio.getframerate(), audio.getnchannels(), audio.getsampwidth(), audio.getnframes()), (32000, 1, 2, 8))
        body = json.loads((self.work / "request.json").read_text())
        self.assertEqual((body["text_lang"], body["prompt_lang"]), ("ja", "zh"))
        self.assertFalse(list(self.work.glob("*.part")))
        self.process.stdin.close(); self.process.wait(timeout=5)
        self.assertEqual(self.process.stderr.read(), "")
        self.assertEqual(self.process.stdout.read(), "")

    def test_switch_pair_and_changed_file_reload(self):
        self.assertTrue(self.call(self.request("load"))["ok"])
        second = self.root / "second.ckpt"; second.write_bytes(b"second")
        self.assertTrue(self.call(self.request("load", gptWeights=str(second)))["ok"])
        self.assertTrue(self.call(self.request("load"))["ok"])
        self.gpt.write_bytes(b"updated-weight")
        self.assertTrue(self.call(self.request("load"))["ok"])
        self.assertEqual([Path(x["t2s_weights_path"]).name for x in self.loads()], ["role.ckpt", "second.ckpt", "role.ckpt", "role.ckpt"])

    def test_failure_cleans_partial_and_invalidates_model(self):
        response = self.call(self.request(text="failure"))
        self.assertFalse(response["ok"])
        self.assertNotIn("PRIVATE", json.dumps(response))
        self.assertFalse((self.work / "voice.wav").exists())
        self.assertFalse(list(self.work.glob("*.part")))
        self.assertFalse((self.work / "diagnostics.jsonl").exists())
        self.assertTrue(self.call(self.request())["ok"])
        self.assertEqual(len(self.loads()), 2)

    def test_output_boundary_and_existing_file_preserved(self):
        outside = self.root / "outside.wav"
        self.assertEqual(self.call(self.request(outputPath=str(outside)))["code"], "invalid_output")
        existing = self.work / "existing.wav"; existing.write_bytes(b"retained")
        self.assertEqual(self.call(self.request(outputPath=str(existing)))["code"], "invalid_output")
        self.assertEqual(existing.read_bytes(), b"retained")

    def test_empty_invalid_and_oversized_audio(self):
        for text, code in (("empty", "invalid_audio"), ("invalid", "invalid_audio"), ("silent", "invalid_audio"), ("huge", "audio_too_large")):
            with self.subTest(text=text):
                self.assertEqual(self.call(self.request(text=text))["code"], code)
                self.assertFalse((self.work / "voice.wav").exists())
                self.assertFalse(list(self.work.glob("*.part")))

    def test_offline_and_cache_boundary(self):
        self.assertTrue(self.call(self.request(text="null_device"))["ok"])
        (self.work / "voice.wav").unlink()
        self.assertEqual(self.call(self.request(text="network"))["code"], "offline_resource_missing")
        self.assertEqual(self.call(self.request(text="outside"))["code"], "cache_boundary")
        self.assertFalse((self.engine / "outside.txt").exists())
        self.assertFalse(list(self.work.glob("*.part")))

    def test_input_validation_and_explicit_cuda(self):
        self.assertEqual(self.call(self.request("load", device="cuda"))["code"], "cuda_unavailable")
        self.assertEqual(self.call(self.request("load", gptWeights="missing.ckpt"))["code"], "invalid_path")
        self.assertEqual(self.call(self.request(language="unsupported"))["code"], "invalid_language")
        self.assertEqual(self.call(self.request(text=""))["code"], "invalid_text")

    def test_output_language_is_independent_of_reference(self):
        self.assertTrue(self.call(self.request(language="jp", japanese=False))["ok"])
        body = json.loads((self.work / "request.json").read_text())
        self.assertEqual((body["text_lang"], body["prompt_lang"]), ("auto", "ja"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
