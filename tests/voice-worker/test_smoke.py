"""Diagnostic tooling must preserve private settings and contain child processes."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import wave

PROJECT = Path(__file__).resolve().parents[2]


def module(name, source):
    spec = importlib.util.spec_from_file_location(name, PROJECT / source)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


smoke = module("smoke_voice", "scripts/smoke-embedded-voice.py")
worker = module("voice_worker", "src/Qiban.Desktop/VoiceEngine/gpt_sovits_worker.py")


class SmokeToolTest(unittest.TestCase):
    def setUp(self):
        base = PROJECT / ".build/worker-tests"
        base.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="diagnostic-", dir=base)
        self.root = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def test_state_selection_excludes_credentials_and_chat(self):
        files = [self.root / name for name in ("role.ckpt", "role.pth", "voice.wav")]
        for path in files:
            path.write_bytes(b"fixture")
        settings = dict(zip(("GptWeights", "SovitsWeights", "ReferenceAudio"), map(str, files)))
        settings.update(ApiKey="must-not-cross", ReferenceText="reference", Language="zh")
        state = self.root / "state.json"
        state.write_text(json.dumps({"SelectedFriendId": "selected", "Friends": [
            {"Id": "other", "Voice": {**settings, "ReferenceText": "other"}},
            {"Id": "selected", "Voice": settings, "Messages": [{"Text": "private-chat"}]}]}), encoding="utf-8")
        voice = smoke.voice_from_state(state)
        self.assertEqual(voice["referenceText"], "reference")
        self.assertEqual(set(voice), {"gptWeights", "sovitsWeights", "referenceAudio", "referenceText", "language"})
        self.assertNotIn("must-not-cross", json.dumps(voice))
        self.assertNotIn("private-chat", json.dumps(voice))

    def test_diagnostics_redact_inputs_and_remain_bounded(self):
        request = {"method": "synthesize", "text": "测试说话内容", "referenceText": "独立参考文本",
                   "referenceAudio": str(self.root / "private.wav"), "gptWeights": str(self.root / "private.ckpt")}
        values = [value for key, value in request.items() if key != "method"]
        try:
            raise RuntimeError(" ".join(values + [json.dumps(value, ensure_ascii=True) for value in values]))
        except RuntimeError as error:
            worker.diagnostic(self.root, error, request)
        data = (self.root / "diagnostics.jsonl").read_text(encoding="utf-8")
        for value in values:
            self.assertNotIn(value, data)
            self.assertNotIn(json.dumps(value)[1:-1], data)
        self.assertIn("RuntimeError", data)
        self.assertIn("<referenceText>", data)
        for _ in range(20):
            worker.diagnostic(self.root, RuntimeError("x" * 30000), request)
        self.assertLessEqual((self.root / "diagnostics.jsonl").stat().st_size, 128 * 1024)

    def test_wave_check_rejects_truncated_payload(self):
        target = self.root / "sample.wav"
        with wave.open(str(target), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(32000)
            audio.writeframes(b"\0\x01" * 100)
        self.assertEqual(smoke.verify_wav(target)["sampleRate"], 32000)
        target.write_bytes(target.read_bytes()[:-8])
        with self.assertRaises(RuntimeError):
            smoke.verify_wav(target)

    def test_wave_check_rejects_upstream_silent_failure(self):
        target = self.root / "silence.wav"
        with wave.open(str(target), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(32000)
            audio.writeframes(b"\0\0" * 9600)
        with self.assertRaisesRegex(RuntimeError, "only silence"):
            smoke.verify_wav(target)

    @unittest.skipUnless(os.name == "nt", "Windows JobObject")
    def test_job_close_terminates_owned_worker(self):
        process = subprocess.Popen([sys.executable, "-B", "-c", "input()"], stdin=subprocess.PIPE,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            job = smoke.ProcessJob(process)
            job.close()
            self.assertIsNotNone(process.wait(timeout=5))
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            process.stdin.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
