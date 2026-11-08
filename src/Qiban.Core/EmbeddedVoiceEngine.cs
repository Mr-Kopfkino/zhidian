using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("Qiban.NetworkTests")]

namespace Qiban.Core;

/// <summary>Owns a private, pipe-only GPT-SoVITS process and its bounded session directory.</summary>
internal sealed class EmbeddedVoiceEngine : IDisposable
{
    private const int MaximumAudioBytes = 32 * 1024 * 1024;
    private readonly string root;
    private readonly Func<string, string, ProcessStartInfo>? testStart;
    private readonly Action? beforePublish;
    private readonly object gate = new();
    private readonly Timer idleTimer;
    private Worker? worker;
    private bool disposed;

    internal EmbeddedVoiceEngine(string applicationRoot, Func<string, string, ProcessStartInfo>? testStart = null, Action? beforePublish = null)
    {
        root = Path.GetFullPath(applicationRoot);
        this.testStart = testStart;
        this.beforePublish = beforePublish;
        idleTimer = new(_ => StopIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal bool IsRunning { get { lock (gate) return worker is { } current && !current.Process.HasExited; } }

    internal async Task<string?> ExecuteAsync(VoiceSettings settings, string? text, bool japanese, string? destination, CancellationToken ct)
    {
        ValidateFile(settings.GptWeights, ".ckpt", "GPT 权重");
        ValidateFile(settings.SovitsWeights, ".pth", "SoVITS 权重");
        if (text != null) ValidateFile(settings.ReferenceAudio, null, "参考音频");
        ct.ThrowIfCancellationRequested();
        Worker current;
        try { current = Start(); }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        using var cancel = ct.Register(() => Stop(current));
        string? output = null;
        try
        {
            if (!current.Ready)
            {
                await WriteAsync(current, new { id = "init", method = "initialize" }, ct).ConfigureAwait(false);
                using var ready = await ReadAsync(current, "ready", ct).ConfigureAwait(false);
                current.Ready = true;
            }
            string id = Guid.NewGuid().ToString("N");
            output = text == null ? null : Path.Combine(current.Directory, "voice-" + id + ".wav");
            await WriteAsync(current, new
            {
                id, method = text == null ? "load" : "synthesize", gptWeights = settings.GptWeights,
                sovitsWeights = settings.SovitsWeights, referenceAudio = settings.ReferenceAudio,
                referenceText = settings.ReferenceText, language = settings.Language, text, japanese,
                outputPath = output, device = "auto"
            }, ct).ConfigureAwait(false);
            using var reply = await ReadAsync(current, id, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (output != null)
            {
                if (!reply.RootElement.TryGetProperty("outputPath", out var returned) || returned.GetString() != output)
                    throw new IOException("内置语音引擎返回了无效的音频路径。");
                // Publish outside the disposable session before Stop/idle cleanup can acquire
                // ownership. This also prevents cleanup racing a temporarily open WAV handle.
                lock (gate)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(worker, current)) throw new IOException("内置语音引擎已停止。");
                    EnsureSafePath(output);
                    var info = new FileInfo(output);
                    if (!info.Exists || info.Length < 44 || info.Length > MaximumAudioBytes)
                        throw new IOException("内置语音音频为空或超过 32 MB 限制。");
                    using (var file = File.OpenRead(output))
                    {
                        byte[] header = new byte[12];
                        if (file.Read(header) != 12 || !header.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                            || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                            throw new IOException("内置语音引擎没有返回有效 WAV 音频。");
                    }
                    beforePublish?.Invoke();
                    ct.ThrowIfCancellationRequested();
                    EnsureSafePath(destination!);
                    File.Move(output, destination!);
                }
            }
            return destination;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Stop(current);
            throw new OperationCanceledException(ct);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Stop(current); // No failed/partially loaded pipeline can be reused by the next character.
            if (error is IOException) throw;
            throw new IOException("内置语音引擎异常退出，请重试加载模型。", error);
        }
        finally { lock (gate) { current.Busy = false; current.LastUsed = DateTimeOffset.UtcNow; } }
    }

    private Worker Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is { } existing && !existing.Process.HasExited) { existing.Busy = true; return existing; }
            if (worker != null) { Cleanup(worker); worker = null; }
            string engine = Path.Combine(root, "engines", "gpt-sovits");
            string python = Path.Combine(engine, "runtime", "python.exe");
            string script = Path.Combine(root, "VoiceEngine", "gpt_sovits_worker.py");
            if (testStart == null && (!File.Exists(python) || !File.Exists(script)
                || !File.Exists(Path.Combine(engine, "GPT_SoVITS", "TTS_infer_pack", "TTS.py"))))
                throw new IOException("内置 GPT-SoVITS 运行组件不完整，请使用包含 engines 文件夹的完整版。");
            EnsureSafePath(python); EnsureSafePath(script);
            string session = Path.Combine(root, "cache", "voice-engine", Guid.NewGuid().ToString("N"));
            EnsureSafePath(session);
            Directory.CreateDirectory(session);
            Process? process = null;
            JobHandle? job = null;
            try
            {
                var start = testStart?.Invoke(engine, session) ?? new ProcessStartInfo(python) { WorkingDirectory = engine };
                if (testStart == null)
                {
                    foreach (var arg in new[] { "-s", "-B", "-u", script, "--engine-root", engine, "--work-root", session })
                        start.ArgumentList.Add(arg);
                }
                start.UseShellExecute = false; start.CreateNoWindow = true;
                start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
                start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = new UTF8Encoding(false);
                ConfigureEnvironment(start, session, engine);
                // The worker waits on stdin before imports. Assign the job before releasing that barrier.
                job = OperatingSystem.IsWindows() ? CreateKillJob() : null;
                process = Process.Start(start) ?? throw new IOException("无法启动内置语音引擎。");
                if (job != null && !AssignProcessToJobObject(job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var current = new Worker(process, job, session);
                _ = DrainDiagnosticsAsync(process.StandardError);
                worker = current;
                idleTimer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
                return current;
            }
            catch (Exception error)
            {
                job?.Dispose();
                if (process != null)
                {
                    try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } } catch (InvalidOperationException) { } catch (Win32Exception) { }
                    process.Dispose();
                }
                DeleteSession(session);
                if (error is IOException) throw;
                throw new IOException("无法启动内置语音引擎，请检查运行组件和目录权限。", error);
            }
        }
    }

    private static void ConfigureEnvironment(ProcessStartInfo start, string session, string engine)
    {
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR", "APPDATA", "LOCALAPPDATA", "USERPROFILE", "HOME", "XDG_CACHE_HOME",
            "HF_HOME", "HF_HUB_CACHE", "HUGGINGFACE_HUB_CACHE", "TRANSFORMERS_CACHE", "TORCH_HOME", "TORCHINDUCTOR_CACHE_DIR",
            "TRITON_CACHE_DIR", "NUMBA_CACHE_DIR", "MPLCONFIGDIR", "PIP_CACHE_DIR", "PYTHONPYCACHEPREFIX", "CUDA_CACHE_PATH",
            "PYTHONUSERBASE", "NLTK_DATA", "JIEBA_CACHE_DIR" })
        {
            string directory = Path.Combine(session, key.ToLowerInvariant());
            Directory.CreateDirectory(directory);
            start.Environment[key] = directory;
        }
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        start.Environment["TOKENIZERS_PARALLELISM"] = "false";
        start.Environment.Remove("PYTHONHOME");
        start.Environment.Remove("PYTHONPATH");
        start.Environment.TryGetValue("PATH", out var inheritedPath);
        start.Environment["PATH"] = string.Join(Path.PathSeparator, new[] { Path.Combine(engine, "runtime"),
            Path.Combine(engine, "runtime", "Scripts"), engine, Path.Combine(engine, "ffmpeg", "bin"),
            inheritedPath ?? "" });
    }

    private static async Task WriteAsync(Worker current, object request, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(request);
        await current.Process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        await current.Process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadAsync(Worker current, string expectedId, CancellationToken ct)
    {
        // ReadLineAsync alone permits unbounded allocation if a dependency accidentally writes to stdout.
        var line = new StringBuilder();
        var buffer = new char[1];
        while (true)
        {
            int count = await current.Process.StandardOutput.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) throw new IOException("内置语音引擎已退出，请检查运行组件与模型是否完整。");
            if (buffer[0] == '\n') break;
            if (buffer[0] != '\r') line.Append(buffer[0]);
            if (line.Length > 8192) throw new IOException("内置语音引擎响应超过限制。");
        }
        var json = JsonDocument.Parse(line.ToString(), new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id) || id.GetString() != expectedId
            || !root.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { json.Dispose(); throw new IOException("内置语音引擎响应无效。"); }
        if (!ok.GetBoolean())
        {
            string code = root.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            json.Dispose();
            // Never display arbitrary worker stdout/stderr, which may contain prompts or private paths.
            throw new IOException(code switch
            {
                "out_of_memory" => "内置语音显存或内存不足，请关闭占用资源的程序后重试。",
                "missing_dependency" or "engine_missing" => "内置语音依赖不完整，请使用完整运行组件。",
                "missing_model" or "missing_file" => "内置语音模型或参考文件缺失，请检查所选文件。",
                "invalid_audio" => "参考音频无法读取，请检查音频格式与时长。",
                "load_failed" => "内置语音权重加载失败，请检查 GPT 与 SoVITS 模型组合及版本。",
                "offline_resource_missing" => "内置语音基础模型不完整，请使用包含基础模型的完整版。",
                "unsupported_model" => "该权重版本与内置语音引擎不兼容，请更换匹配的模型组合。",
                "cuda_unavailable" => "语音加速设备不可用，请检查显卡驱动后重试。",
                "audio_too_large" => "合成音频超过 32 MB，请缩短朗读文本。",
                "cache_boundary" => "内置语音缓存路径无效，请检查程序目录权限。",
                _ => "内置语音处理失败，请检查模型与参考音频后重试。"
            });
        }
        return json;
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        try { char[] buffer = new char[2048]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (IOException) { } catch (ObjectDisposedException) { }
    }

    internal void Stop() { lock (gate) { if (worker is { } current) { worker = null; Cleanup(current); } } }
    private void StopIdle()
    {
        lock (gate)
            if (worker is { Busy: false } current && DateTimeOffset.UtcNow - current.LastUsed >= TimeSpan.FromSeconds(60))
            { worker = null; Cleanup(current); }
    }
    private void Stop(Worker current) { lock (gate) { if (ReferenceEquals(worker, current)) { worker = null; Cleanup(current); } } }
    private void Cleanup(Worker current)
    {
        current.Job?.Dispose(); // Closing a job also terminates descendants after a host crash.
        try { if (!current.Process.HasExited) current.Process.Kill(entireProcessTree: true); current.Process.WaitForExit(5000); }
        catch (InvalidOperationException) { } catch (Win32Exception) { }
        current.Process.Dispose();
        DeleteSession(current.Directory);
    }

    private void DeleteSession(string path)
    {
        try
        {
            string boundary = Path.Combine(root, "cache", "voice-engine") + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) return;
            EnsureSafePath(path);
            if (!Directory.Exists(path)) return;
            // Refuse redirection anywhere in the owned subtree; startup cleanup can revisit locked files.
            var pending = new Stack<string>(); pending.Push(path);
            while (pending.TryPop(out var directory))
                foreach (var item in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
                }
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void ValidateFile(string value, string? extension, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !Path.IsPathFullyQualified(value)
            || (extension != null && !value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) || !File.Exists(value))
            throw new InvalidDataException($"请选择有效的本机{label}文件。" );
    }

    private static void EnsureSafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("内置语音目录不能使用符号链接或目录联接。");
    }

    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; idleTimer.Dispose(); Stop(); } }

    private sealed class Worker(Process process, JobHandle? job, string directory)
    {
        internal Process Process { get; } = process;
        internal JobHandle? Job { get; } = job;
        internal string Directory { get; } = directory;
        internal bool Ready { get; set; }
        internal bool Busy { get; set; } = true;
        internal DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }

    private static JobHandle CreateKillJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits = new JobExtendedLimits { Basic = new JobBasicLimits { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()))
        { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return job;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    { public JobBasicLimits Basic; public IoCounters Io; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref JobExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
