"""Exercise the private worker using local role weights and fixed smoke-test text.

Never emits keys, role names, reference text or chat records. --diagnostics enables
redacted exception traces in the project-local work directory, not on stdout.
"""
from __future__ import annotations
import argparse
from array import array
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time
import wave

PROJECT = Path(__file__).resolve().parents[1]


class ProcessJob:
    """Tie native ffmpeg/inference descendants to this diagnostic run on Windows."""
    def __init__(self, process):
        self.handle = None
        if os.name != "nt":
            return
        import ctypes
        from ctypes import wintypes
        class Limits(ctypes.Structure):
            _fields_ = [("processTime", ctypes.c_int64), ("jobTime", ctypes.c_int64), ("flags", wintypes.DWORD),
                        ("minimumWorkingSet", ctypes.c_size_t), ("maximumWorkingSet", ctypes.c_size_t),
                        ("activeProcesses", wintypes.DWORD), ("affinity", ctypes.c_size_t),
                        ("priority", wintypes.DWORD), ("schedulingClass", wintypes.DWORD)]
        class ExtendedLimits(ctypes.Structure):
            _fields_ = [("basic", Limits), ("ioCounters", ctypes.c_uint64 * 6),
                        ("processMemory", ctypes.c_size_t), ("jobMemory", ctypes.c_size_t),
                        ("peakProcessMemory", ctypes.c_size_t), ("peakJobMemory", ctypes.c_size_t)]
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        self.kernel.CreateJobObjectW.restype = wintypes.HANDLE
        self.kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        self.kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.handle = self.kernel.CreateJobObjectW(None, None)
        limits = ExtendedLimits()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.handle or not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)) \
                or not self.kernel.AssignProcessToJobObject(self.handle, int(process._handle)):
            self.close()
            raise RuntimeError("Unable to contain the diagnostic worker process")

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def voice_from_state(file: Path) -> dict:
    state = json.loads(file.read_text(encoding="utf-8-sig"))
    selected = state.get("SelectedFriendId")
    friends = sorted(state.get("Friends", []), key=lambda friend: friend.get("Id") != selected)
    for friend in friends:
        voice = friend.get("Voice", {})
        if all(isinstance(voice.get(key), str) and Path(voice[key]).is_file()
               for key in ("GptWeights", "SovitsWeights", "ReferenceAudio")):
            # Only these noncredential settings cross the process boundary.
            return {"gptWeights": voice["GptWeights"], "sovitsWeights": voice["SovitsWeights"],
                    "referenceAudio": voice["ReferenceAudio"], "referenceText": voice.get("ReferenceText", ""),
                    "language": voice.get("Language", "zh")}
    raise RuntimeError("No role has an existing weight pair and reference audio")


def verify_wav(path: Path) -> dict:
    size = path.stat().st_size
    if not 44 < size <= 32 * 1024 * 1024:
        raise RuntimeError("Invalid audio file size")
    with wave.open(str(path)) as audio:
        frames, rate = audio.getnframes(), audio.getframerate()
        if frames <= 0 or not 8000 <= rate <= 192000 or audio.getnchannels() != 1 or audio.getsampwidth() != 2:
            raise RuntimeError("Invalid WAV parameters")
        payload = audio.readframes(frames)
        if len(payload) != frames * 2:
            raise RuntimeError("Truncated WAV payload")
    samples = array("h", payload)
    if sys.byteorder != "little":
        samples.byteswap()
    peak = max(abs(value) for value in samples)
    if peak == 0:
        raise RuntimeError("WAV contains only silence")
    return {"bytes": size, "sampleRate": rate, "durationSeconds": round(frames / rate, 3), "peakAmplitude": peak}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine-root", required=True, type=Path)
    parser.add_argument("--worker", default=PROJECT / "src/Qiban.Desktop/VoiceEngine/gpt_sovits_worker.py", type=Path)
    parser.add_argument("--state-file", required=True, type=Path)
    parser.add_argument("--work-root", required=True, type=Path)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    parser.add_argument("--timeout", type=float, default=300)
    parser.add_argument("--diagnostics", action="store_true")
    parser.add_argument("--languages", nargs="+", choices=("zh", "ja"), default=["zh", "ja"])
    args = parser.parse_args()
    work = args.work_root.resolve()
    if not work.is_relative_to(PROJECT) or work == PROJECT or work.exists():
        raise RuntimeError("Choose a new work directory inside this project")
    engine = args.engine_root.resolve(strict=True)
    worker = args.worker.resolve(strict=True)
    python = engine / "runtime/python.exe"
    if not python.is_file():
        raise RuntimeError("Bundled Python is unavailable")
    voice = voice_from_state(args.state_file)
    work.mkdir(parents=True)
    environment = os.environ.copy()
    for key in ("TEMP", "TMP", "TMPDIR", "APPDATA", "LOCALAPPDATA", "USERPROFILE", "HOME", "PYTHONUSERBASE",
                "HF_HOME", "TORCH_HOME", "XDG_CACHE_HOME", "CUDA_CACHE_PATH", "TRITON_CACHE_DIR"):
        directory = work / "bootstrap" / key.lower()
        directory.mkdir(parents=True, exist_ok=True)
        environment[key] = str(directory)
    environment.update(PYTHONDONTWRITEBYTECODE="1", PYTHONNOUSERSITE="1", HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
    command = [str(python), "-s", "-B", "-u", str(worker), "--engine-root", str(engine), "--work-root", str(work)]
    if args.diagnostics: command.append("--diagnostics")
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                               text=True, encoding="utf-8", env=environment, cwd=engine,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        job = ProcessJob(process)
    except BaseException:
        process.kill()
        process.wait(timeout=5)
        process.stdin.close()
        process.stdout.close()
        raise
    responses = queue.Queue()
    def read_responses():
        try:
            for line in process.stdout:
                responses.put(line)
        finally:
            responses.put(None)
    reader = threading.Thread(target=read_responses, daemon=True)
    reader.start()

    def request(method: str, identity: str, **fields) -> dict:
        started = time.monotonic()
        process.stdin.write(json.dumps({"id": identity, "method": method, **fields}, ensure_ascii=False) + "\n")
        process.stdin.flush()
        try:
            line = responses.get(timeout=args.timeout)
        except queue.Empty:
            raise RuntimeError(f"{method}: worker timeout") from None
        if line is None:
            raise RuntimeError(f"{method}: worker exited")
        try:
            result = json.loads(line)
        except ValueError:
            raise RuntimeError(f"{method}: invalid worker protocol") from None
        expected = "ready" if method == "initialize" else identity
        if result.get("id") != expected or result.get("ok") is not True:
            # Whitelist protocol codes; never print a server-provided error string.
            code = result.get("code", "failed")
            if not isinstance(code, str) or len(code) > 64 or not all(c.islower() or c == "_" for c in code): code = "failed"
            raise RuntimeError(f"{method}: {code}")
        print(json.dumps({"stage": identity, "passed": True, "elapsedSeconds": round(time.monotonic() - started, 2)}), flush=True)
        return result

    try:
        request("initialize", "initialize")
        request("load", "load", **voice, device=args.device)
        languages = list(dict.fromkeys(args.languages))
        fixed_texts = {"zh": "你好。这是内置语音合成测试。", "ja": "こんにちは。今日はいい天気ですね。"}
        for language in languages:
            output = work / (language + ".wav")
            result = request("synthesize", language, **voice, text=fixed_texts[language], japanese=language == "ja",
                             textLanguage=language, outputPath=str(output), device=args.device)
            if result.get("outputPath") != str(output):
                raise RuntimeError("Unexpected output path")
            print(json.dumps({"stage": language, "audio": verify_wav(output)}), flush=True)
        (work / "smoke-result.json").write_text(json.dumps({"passed": True, "languages": languages}), encoding="utf-8")
        return 0
    except (RuntimeError, OSError) as error:
        # All displayed RuntimeErrors above contain only our own bounded messages.
        print(json.dumps({"passed": False, "stageError": str(error) if isinstance(error, RuntimeError) else type(error).__name__,
                          "diagnostics": "enabled" if args.diagnostics else "disabled"}), flush=True)
        return 1
    finally:
        try:
            process.stdin.close()
        except OSError:
            pass
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            job.close()
            process.kill()
            process.wait(timeout=5)
        finally:
            job.close()
        process.stdout.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, KeyError, json.JSONDecodeError) as error:
        print(json.dumps({"passed": False, "setupError": type(error).__name__}), flush=True)
        raise SystemExit(1)
