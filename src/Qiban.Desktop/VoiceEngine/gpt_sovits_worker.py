"""Private JSONL inference worker. The desktop owns its process and work directory.

Uses GPT-SoVITS' TTS class directly; it never starts an HTTP server. Third-party
stdout/stderr are suppressed because upstream diagnostics can contain spoken text.
"""
from __future__ import annotations

import argparse
import gc
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
import traceback
import wave

MAX_AUDIO_BYTES = 32 * 1024 * 1024
MAX_REQUEST_BYTES = 256 * 1024
LANGUAGES = {"zh", "en", "ja", "yue", "ko", "all_zh", "all_ja", "all_yue", "all_ko", "auto", "auto_yue"}


class WorkerError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def path_without_links(value: str) -> Path:
    path = Path(value).absolute()
    for item in (path, *path.parents):
        try:
            info = item.lstat()
        except FileNotFoundError:
            continue
        if item.is_symlink() or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise WorkerError("unsafe_path", "语音路径不能使用符号链接或目录联接。")
    return path.resolve()


def file_input(request: dict, name: str, extensions: set[str]) -> Path:
    value = request.get(name)
    if not isinstance(value, str) or not value or len(value) > 4096 or not Path(value).is_absolute():
        raise WorkerError("invalid_path", "请填写模型和参考音频的完整文件路径。")
    path = path_without_links(value)
    if path.suffix.lower() not in extensions or not path.is_file():
        raise WorkerError("missing_file", "模型或参考音频文件不存在，或格式不支持。")
    return path


def text_input(request: dict, name: str, maximum: int, *, required: bool = True) -> str:
    value = request.get(name, "")
    if not isinstance(value, str) or len(value) > maximum or (required and not value.strip()):
        raise WorkerError("invalid_text", "语音文本为空或超过长度限制。")
    return value


def configure_environment(engine: Path, work: Path) -> None:
    # Set before importing any inference dependency. No machine-wide settings change.
    for key, child in {
        "TEMP": "tmp", "TMP": "tmp", "TMPDIR": "tmp", "APPDATA": "profile/roaming",
        "LOCALAPPDATA": "profile/local", "USERPROFILE": "profile", "HOME": "profile",
        "XDG_CACHE_HOME": "external", "HF_HOME": "huggingface", "HUGGINGFACE_HUB_CACHE": "huggingface/hub",
        "HF_HUB_CACHE": "huggingface/hub", "TRANSFORMERS_CACHE": "huggingface/transformers",
        "TORCH_HOME": "torch", "TORCH_EXTENSIONS_DIR": "torch/extensions", "TORCHINDUCTOR_CACHE_DIR": "torch/inductor",
        "TRITON_CACHE_DIR": "triton", "NUMBA_CACHE_DIR": "numba", "MPLCONFIGDIR": "matplotlib",
        "CUDA_CACHE_PATH": "cuda", "PYTHONPYCACHEPREFIX": "python", "PIP_CACHE_DIR": "pip",
    }.items():
        directory = work / child
        directory.mkdir(parents=True, exist_ok=True)
        os.environ[key] = str(directory)
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_DATASETS_OFFLINE="1",
                      HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1", PYTHONDONTWRITEBYTECODE="1",
                      TOKENIZERS_PARALLELISM="false", G2PW="0", PYTHONNOUSERSITE="1")
    os.environ["LAIDIAN_VOICE_WORK"] = str(work)
    os.environ["NLTK_DATA"] = str(engine / "runtime/nltk_data")
    os.environ["PATH"] = str(engine / "runtime") + os.pathsep + os.environ.get("PATH", "")
    sys.dont_write_bytecode = True
    tempfile.tempdir = str(work / "tmp")
    sys.path[:0] = [str(engine), str(engine / "GPT_SoVITS")]
    os.chdir(engine)

    # Known libraries receive explicit cache paths. The audit hook fails closed
    # if a dependency still attempts a Python-level write outside this session.
    def writable(value: object, *, existing_directory: bool = False) -> None:
        if isinstance(value, int):  # Already-open protocol/temp file descriptor.
            return
        path = Path(os.fsdecode(value)).absolute().resolve()
        if existing_directory and path.is_dir():
            return
        if not path.is_relative_to(work):
            raise WorkerError("cache_boundary", "语音组件尝试写入缓存目录之外，操作已停止。")

    def offline(event: str, args: tuple) -> None:
        if event in {"socket.connect", "socket.connect_ex", "socket.bind", "socket.getaddrinfo"}:
            raise WorkerError("offline_resource_missing", "内置引擎缺少离线资源，请重新安装完整语音组件。")
        if event == "open" and args[2] & (os.O_WRONLY | os.O_RDWR | os.O_CREAT | os.O_TRUNC | os.O_APPEND):
            # subprocess uses the OS null device for discarded child output. It
            # stores no bytes; only this exact device name bypasses file checks.
            if not isinstance(args[0], int) and os.path.normcase(os.fsdecode(args[0])) == os.path.normcase(os.devnull):
                return
            writable(args[0])
        elif event == "os.mkdir":
            writable(args[0], existing_directory=True)
        elif event in {"os.remove", "os.rmdir", "os.chmod", "os.utime", "os.truncate"}:
            writable(args[0])
        elif event in {"os.rename", "os.link", "os.symlink"}:
            writable(args[0])
            writable(args[1])
    sys.addaudithook(offline)


class Engine:
    def __init__(self, root: Path, work: Path):
        self.root, self.work = root, work
        self.pipeline = None
        self.signature = None
        self.torch = None

    def unload(self) -> None:
        self.pipeline = None
        self.signature = None
        gc.collect()
        if self.torch is not None and self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()

    def load(self, request: dict) -> None:
        gpt = file_input(request, "gptWeights", {".ckpt"})
        sovits = file_input(request, "sovitsWeights", {".pth"})
        device = request.get("device", "auto")
        if device not in {"auto", "cpu", "cuda"}:
            raise WorkerError("invalid_device", "不支持的语音运算设备。")
        signature = tuple((str(p), p.stat().st_size, p.stat().st_mtime_ns) for p in (gpt, sovits)) + (device,)
        if self.pipeline is not None and self.signature == signature:
            return
        self.unload()  # Never continue with a partially switched or previous role's model.
        try:
            # Newer official packages prepare their DLL directories here. Older
            # packages use runtime on PATH and do not contain this helper.
            if (self.root / "tools/portable_runtime.py").is_file():
                from tools.portable_runtime import activate
                activate(change_cwd=True)
            import torch
            from TTS_infer_pack.TTS import TTS, TTS_Config
            from process_ckpt import get_sovits_version_from_path_fast
            self.torch = torch
            use_cuda = torch.cuda.is_available() and device != "cpu"
            if device == "cuda" and not use_cuda:
                raise WorkerError("cuda_unavailable", "CUDA 不可用，请检查显卡驱动后重试。")
            version = get_sovits_version_from_path_fast(str(sovits))[1]
            if version not in TTS_Config.default_configs:
                raise WorkerError("unsupported_model", "当前内置引擎不支持此模型版本。")
            pretrained = self.root / "GPT_SoVITS/pretrained_models"
            bert, hubert = pretrained / "chinese-roberta-wwm-ext-large", pretrained / "chinese-hubert-base"
            if not all((p / "config.json").is_file() for p in (bert, hubert)):
                raise WorkerError("offline_resource_missing", "内置引擎缺少 BERT 或 HuBERT 基础模型。")
            config = TTS_Config({"custom": {
                "version": version, "device": "cuda" if use_cuda else "cpu", "is_half": use_cuda,
                "t2s_weights_path": str(gpt), "vits_weights_path": str(sovits),
                "bert_base_path": str(bert), "cnhuhbert_base_path": str(hubert),
            }})
            # Upstream init_* methods save configuration while loading. Keep these
            # transient role paths out of the shared engine and user snapshots.
            config.configs_path = str(self.work / "tts_infer.yaml")
            self.pipeline = TTS(config)
            self.signature = signature
        except BaseException:
            self.unload()
            raise

    def output_path(self, request: dict) -> Path:
        value = request.get("outputPath")
        if not isinstance(value, str) or not Path(value).is_absolute():
            raise WorkerError("invalid_output", "语音输出路径无效。")
        path = path_without_links(value)
        if not path.is_relative_to(self.work) or path == self.work or path.suffix.lower() != ".wav" or path.exists():
            raise WorkerError("invalid_output", "语音输出路径必须是缓存目录内的新 WAV 文件。")
        path.parent.mkdir(parents=True, exist_ok=True)
        return path

    def synthesize(self, request: dict) -> str:
        text = text_input(request, "text", 16000)
        reference_text = text_input(request, "referenceText", 16000, required=False)
        reference = file_input(request, "referenceAudio", {".wav", ".mp3", ".flac", ".ogg"})
        language = request.get("language", "zh")
        language = {"jp": "ja", "all_jp": "all_ja"}.get(language, language) if isinstance(language, str) else None
        text_language = request.get("textLanguage", "auto")
        japanese = request.get("japanese", False)
        if language not in LANGUAGES or not isinstance(text_language, str) or text_language not in LANGUAGES or not isinstance(japanese, bool):
            raise WorkerError("invalid_language", "不支持的语音或参考音频语言。")
        destination = self.output_path(request)
        partial = destination.with_name(destination.name + ".part")
        if partial.exists():
            raise WorkerError("invalid_output", "语音临时文件已存在，请重试。")
        try:
            self.load(request)
            import numpy as np
            inputs = {
                "text": text, "text_lang": "ja" if japanese else text_language,
                "ref_audio_path": str(reference), "prompt_text": reference_text, "prompt_lang": language,
                "text_split_method": "cut5", "batch_size": 1, "parallel_infer": False,
                "split_bucket": True, "streaming_mode": False, "return_fragment": False,
                "speed_factor": 1.0, "seed": -1, "super_sampling": False,
                "use_flash_attention": False, "use_cuda_graph": False,
            }
            count, frames, rate = 0, 0, None
            has_signal = False
            with partial.open("xb") as file:
                with wave.open(file, "wb") as output:
                    output.setparams((1, 2, 32000, 0, "NONE", "not compressed"))
                    for sample_rate, audio in self.pipeline.run(inputs):
                        if not isinstance(sample_rate, (int, np.integer)) or not 8000 <= sample_rate <= 192000:
                            raise WorkerError("invalid_audio", "语音引擎返回了无效的采样率。")
                        if rate is not None and rate != sample_rate:
                            raise WorkerError("invalid_audio", "语音片段的采样率不一致。")
                        rate = int(sample_rate)
                        samples = np.asarray(audio)
                        if samples.ndim != 1 or not (np.issubdtype(samples.dtype, np.integer) or np.issubdtype(samples.dtype, np.floating)) or not np.isfinite(samples).all():
                            raise WorkerError("invalid_audio", "语音引擎返回了无效的音频。")
                        if np.issubdtype(samples.dtype, np.floating):
                            samples = np.rint(np.clip(samples, -1.0, 1.0) * 32767)
                        pcm_samples = samples.astype("<i2", copy=False)
                        pcm = pcm_samples.tobytes()
                        count += len(pcm)
                        if count + 44 > MAX_AUDIO_BYTES:
                            raise WorkerError("audio_too_large", "语音文件超过 32 MB 限制，请缩短文本。")
                        has_signal = has_signal or bool(np.any(pcm_samples))
                        if frames == 0:
                            output.setparams((1, 2, rate, 0, "NONE", "not compressed"))
                        output.writeframesraw(pcm)
                        frames += len(samples)
                file.flush()
                os.fsync(file.fileno())
            if not frames or not has_signal:
                raise WorkerError("invalid_audio", "语音引擎没有生成有效音频。")
            # Windows rename refuses an existing destination and publishes the
            # complete WAV atomically. On POSIX, link gives the same no-overwrite guarantee.
            if os.name == "nt":
                os.rename(partial, destination)
            else:
                os.link(partial, destination)
            return str(destination)
        except BaseException:
            self.unload()
            raise
        finally:
            partial.unlink(missing_ok=True)


def error_response(error: Exception, phase: str) -> dict:
    if isinstance(error, WorkerError):
        return {"ok": False, "code": error.code, "error": str(error)}
    if isinstance(error, (ImportError, ModuleNotFoundError)):
        return {"ok": False, "code": "missing_dependency", "error": "内置语音依赖不完整，请重新安装完整语音组件。"}
    if "OutOfMemory" in type(error).__name__ or "out of memory" in str(error).lower():
        return {"ok": False, "code": "out_of_memory", "error": "语音推理内存或显存不足，请关闭占用资源的程序后重试。"}
    if isinstance(error, FileNotFoundError):
        return {"ok": False, "code": "missing_model", "error": "语音模型或离线资源缺失，请检查文件与完整语音组件。"}
    return {"ok": False, "code": "load_failed" if phase == "load" else "inference_failed", "error":
            "语音模型加载失败，请检查权重组合与内置引擎版本。" if phase == "load" else "语音合成失败，请检查参考音频、文本及模型配置。"}


def read_request() -> dict | None:
    line = sys.stdin.buffer.readline(MAX_REQUEST_BYTES + 1)
    if not line:
        return None
    if len(line) > MAX_REQUEST_BYTES:
        raise WorkerError("request_too_large", "语音请求超过长度限制。")
    request = json.loads(line.decode("utf-8"))
    if not isinstance(request, dict):
        raise WorkerError("invalid_request", "语音请求格式无效。")
    return request


def diagnostic(work: Path, error: Exception, request: dict | None) -> None:
    """Explicit developer mode only; never log request bodies or unredacted text."""
    try:
        details = "".join(traceback.format_exception(type(error), error, error.__traceback__))
        if isinstance(request, dict):
            for key in ("text", "referenceText", "referenceAudio", "gptWeights", "sovitsWeights"):
                value = request.get(key)
                if isinstance(value, str) and value:
                    for representation in (value, repr(value)[1:-1], json.dumps(value, ensure_ascii=True)[1:-1]):
                        details = details.replace(representation, f"<{key}>")
        record = {"phase": request.get("method", "initialize") if isinstance(request, dict) else "initialize",
                  "exception": type(error).__name__, "traceback": details[:16000]}
        payload = (json.dumps(record, ensure_ascii=False) + "\n").encode("utf-8")
        target = work / "diagnostics.jsonl"
        if target.exists() and target.stat().st_size + len(payload) > 128 * 1024:
            return
        with target.open("ab") as output:
            output.write(payload)
    except Exception:
        pass  # Diagnostics must not alter inference/protocol failures.


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine-root", required=True)
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--diagnostics", action="store_true", help="Write redacted traces only inside the session directory")
    args = parser.parse_args()
    # Preserve a private protocol descriptor, then silence both Python and native
    # dependency writes to inherited stdout/stderr (including ffmpeg diagnostics).
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
    sink = open(os.devnull, "w", encoding="utf-8")
    os.dup2(sink.fileno(), sys.stdout.fileno())
    os.dup2(sink.fileno(), sys.stderr.fileno())
    sys.stdout = sys.stderr = sink

    def respond(value: dict) -> None:
        protocol.write(json.dumps(value, ensure_ascii=False, allow_nan=False) + "\n")
        protocol.flush()

    work = None
    try:
        initial = read_request()
        if initial is None:
            return 0
        if initial.get("method") != "initialize":
            raise WorkerError("not_initialized", "语音进程尚未初始化。")
        root, work = path_without_links(args.engine_root), path_without_links(args.work_root)
        if not (root / "GPT_SoVITS/TTS_infer_pack/TTS.py").is_file() or root == work or root.is_relative_to(work):
            raise WorkerError("engine_missing", "内置 GPT-SoVITS 引擎缺失。")
        work.mkdir(parents=True, exist_ok=True)
        configure_environment(root, work)
        engine = Engine(root, work)
        respond({"id": "ready", "ok": True})
    except Exception as error:
        if args.diagnostics and work is not None:
            diagnostic(work, error, None)
        respond({"id": "ready", **error_response(error, "load")})
        return 1
    while True:
        request = None
        try:
            request = read_request()
            if request is None:
                return 0
            identity, method = request.get("id"), request.get("method")
            if not isinstance(identity, str) or not 1 <= len(identity) <= 128 or any(ord(c) < 32 for c in identity):
                raise WorkerError("invalid_request", "语音请求标识无效。")
            if method == "load":
                engine.load(request)
                response = {"id": identity, "ok": True}
            elif method == "synthesize":
                response = {"id": identity, "ok": True, "outputPath": engine.synthesize(request)}
            else:
                raise WorkerError("invalid_method", "不支持的语音操作。")
            respond(response)
        except Exception as error:
            if args.diagnostics:
                diagnostic(work, error, request)
            identity = request.get("id", "") if isinstance(request, dict) else ""
            if not isinstance(identity, str) or len(identity) > 128:
                identity = ""
            respond({"id": identity, **error_response(error, request.get("method", "") if isinstance(request, dict) else "")})
            # A malformed/oversized line cannot safely be resynchronized.
            if request is None:
                return 1


if __name__ == "__main__":
    raise SystemExit(main())
