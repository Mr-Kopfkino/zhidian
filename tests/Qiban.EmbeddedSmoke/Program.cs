using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qiban.Core;

// An explicit, real integration smoke test. It never loads AppState/PortableStore,
// saves user data, contacts a speech API, or prints user-supplied configuration.
internal static class Program
{
    private const string Chinese = "你好，今天也一起度过愉快的时光吧。";
    private const string Japanese = "こんにちは。今日はいい天気ですね。";
    private static string stage = "arguments";

    private static async Task<int> Main(string[] args)
    {
        VoiceClient? client = null;
        var ownedWorkers = new Dictionary<int, Process>();
        var audioFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? appRoot = null, stateFile = null;
        byte[]? stateHash = null;
        Dictionary<string, FileStamp>? initialFiles = null;
        int completed = 0;
        bool passed = false;
        string failure = "", errorType = "";
        try
        {
            (appRoot, stateFile) = Arguments(args);
            Require(OperatingSystem.IsWindows(), "windows_required");
            RejectLinks(appRoot);
            Require(Directory.Exists(appRoot), "application_root_missing");
            RejectLinks(stateFile);
            stateHash = HashFile(stateFile);
            stage = "configuration";
            var voices = ReadVoices(stateFile);
            Require(voices.Count > 0, "no_existing_model_pair");
            var existing = FindWorkers(appRoot);
            try { Require(existing.Count == 0, "engine_already_running"); }
            finally { foreach (var process in existing) process.Dispose(); }
            Require(SessionDirectories(appRoot).Length == 0, "existing_session_directories");
            initialFiles = CacheFiles(appRoot);
            string cache = Path.Combine(appRoot, "cache");
            client = new VoiceClient(appRoot);

            stage = "pre_cancelled";
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectCancelled(() => client.MountAsync(voices[0], cancelled.Token));
                await ExpectCancelled(async () =>
                {
                    string unexpected = await client.SynthesizeAsync(voices[0], Chinese, cache, cancelled.Token);
                    audioFiles.Add(unexpected);
                });
            }
            Require(!client.IsEmbeddedRunning, "pre_cancel_started_worker");
            AssertStopped(appRoot, ownedWorkers.Values);
            AssertCacheUnchanged(appRoot, initialFiles);
            Write(new { check = "pre_cancelled", ok = true });

            for (int index = 0; index < voices.Count; index++)
            {
                stage = $"role_{index + 1}_mount";
                await client.MountAsync(voices[index], CancellationToken.None);
                Require(client.IsEmbeddedRunning, "mount_did_not_keep_worker");
                CaptureWorker(appRoot, ownedWorkers);
                Require(SessionDirectories(appRoot).Length == 1, "expected_one_session");
                Write(new { check = "mount", role = index + 1, ok = true });

                foreach (bool japanese in new[] { false, true })
                {
                    stage = $"role_{index + 1}_{(japanese ? "ja" : "zh")}";
                    string? output = null;
                    try
                    {
                        output = await client.SynthesizeAsync(voices[index], japanese ? Japanese : Chinese,
                            cache, CancellationToken.None, japanese);
                        audioFiles.Add(output);
                        EnsureOwnedAudio(appRoot, output);
                        var audio = ValidateWave(output);
                        Write(new { check = "synthesize", role = index + 1, language = japanese ? "ja" : "zh",
                            ok = true, audio.SampleRate, audio.Frames, audio.DurationSeconds, audio.Peak, audio.Rms });
                        completed++;
                    }
                    finally
                    {
                        if (output != null) DeleteAudio(appRoot, output, audioFiles);
                    }
                }
            }

            stage = "stop";
            client.StopEmbedded();
            Require(!client.IsEmbeddedRunning, "stop_flag_still_running");
            AssertStopped(appRoot, ownedWorkers.Values);
            AssertCacheUnchanged(appRoot, initialFiles);
            DisposeHandles(ownedWorkers);
            Write(new { check = "stop_process_and_session_cleanup", ok = true });

            // Restart a real loaded worker, then verify Dispose independently of Stop.
            stage = "dispose_mount";
            await client.MountAsync(voices[0], CancellationToken.None);
            Require(client.IsEmbeddedRunning, "dispose_setup_worker_missing");
            CaptureWorker(appRoot, ownedWorkers);
            stage = "dispose";
            client.Dispose();
            Require(!client.IsEmbeddedRunning, "dispose_flag_still_running");
            AssertStopped(appRoot, ownedWorkers.Values);
            AssertCacheUnchanged(appRoot, initialFiles);
            Require(HashFile(stateFile).AsSpan().SequenceEqual(stateHash), "state_file_changed");
            Write(new { check = "dispose_process_and_session_cleanup", ok = true });
            passed = true;
        }
        catch (Exception error)
        {
            // Arbitrary exception messages/stack traces can expose paths or reference text.
            failure = error is SmokeFailure known ? known.Code : "operation_failed";
            errorType = error.GetType().Name;
        }
        finally
        {
            try
            {
                client?.Dispose();
                if (appRoot != null)
                {
                    foreach (string output in audioFiles.ToArray()) DeleteAudio(appRoot, output, audioFiles);
                    if (initialFiles != null)
                    {
                        AssertStopped(appRoot, ownedWorkers.Values);
                        AssertCacheUnchanged(appRoot, initialFiles);
                    }
                }
                if (stateFile != null && stateHash != null)
                    Require(HashFile(stateFile).AsSpan().SequenceEqual(stateHash), "state_file_changed");
            }
            catch (Exception cleanup)
            {
                passed = false;
                failure = cleanup is SmokeFailure known ? known.Code : "cleanup_failed";
                errorType = cleanup.GetType().Name;
                stage = "cleanup";
            }
            DisposeHandles(ownedWorkers);
        }
        Write(new { check = "result", ok = passed, synthesized = completed,
            stage = passed ? "complete" : stage, code = failure, errorType });
        return passed ? 0 : 1;
    }

    private static (string Root, string State) Arguments(string[] args)
    {
        Require(args.Length == 4 && args[0] == "--app-root" && args[2] == "--state-file", "arguments_required");
        return (Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
    }

    private static List<VoiceSettings> ReadVoices(string path)
    {
        Require(new FileInfo(path).Length is > 0 and <= 128 * 1024 * 1024, "invalid_state_size");
        using var file = File.OpenRead(path);
        using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 32 });
        var friends = Property(document.RootElement, "Friends");
        Require(friends.ValueKind == JsonValueKind.Array, "friends_array_missing");
        var result = new List<VoiceSettings>();
        foreach (var friend in friends.EnumerateArray())
        {
            var voice = Property(friend, "Voice");
            if (voice.ValueKind != JsonValueKind.Object) continue;
            string gpt = Text(voice, "GptWeights"), sovits = Text(voice, "SovitsWeights");
            if (!Path.IsPathFullyQualified(gpt) || !Path.IsPathFullyQualified(sovits)
                || !File.Exists(gpt) || !File.Exists(sovits)) continue;
            result.Add(new VoiceSettings
            {
                Enabled = true, Provider = "GPT-SoVITS 内置", Endpoint = "", ProtectedApiKey = "",
                GptWeights = gpt, SovitsWeights = sovits, ReferenceAudio = Text(voice, "ReferenceAudio"),
                ReferenceText = Text(voice, "ReferenceText"), Language = Text(voice, "Language", "zh")
            });
        }
        return result;
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    private static string Text(JsonElement element, string name, string fallback = "")
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static async Task ExpectCancelled(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new SmokeFailure("pre_cancel_not_observed");
    }

    private static List<Process> FindWorkers(string root)
    {
        string expected = Path.Combine(root, "engines", "gpt-sovits", "runtime", "python.exe");
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName("python"))
        {
            bool keep = false;
            try
            {
                if (!process.HasExited && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                {
                    _ = process.Handle; // Retain the original process handle across PID reuse.
                    result.Add(process);
                    keep = true;
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { if (!keep) process.Dispose(); }
        }
        return result;
    }

    private static void CaptureWorker(string root, Dictionary<int, Process> owned)
    {
        var processes = FindWorkers(root);
        if (processes.Count != 1)
        {
            foreach (var process in processes) process.Dispose();
            throw new SmokeFailure("expected_one_real_python_worker");
        }
        var current = processes[0];
        if (!owned.TryAdd(current.Id, current)) current.Dispose();
    }

    private static void AssertStopped(string root, IEnumerable<Process> owned)
    {
        foreach (var process in owned)
            Require(process.HasExited || process.WaitForExit(5000), "worker_process_survived");
        var remaining = FindWorkers(root);
        try { Require(remaining.Count == 0, "worker_process_remains"); }
        finally { foreach (var process in remaining) process.Dispose(); }
        Require(SessionDirectories(root).Length == 0, "session_directory_remains");
    }

    private static string[] SessionDirectories(string root)
    {
        string path = Path.Combine(root, "cache", "voice-engine");
        RejectLinks(path);
        return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
    }

    private static Dictionary<string, FileStamp> CacheFiles(string root)
    {
        string cache = Path.Combine(root, "cache");
        RejectLinks(cache);
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(cache)) return result;
        var directories = new Stack<string>();
        directories.Push(cache);
        while (directories.TryPop(out var current))
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(path);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "cache_reparse_point");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                else
                {
                    var info = new FileInfo(path);
                    result.Add(path, new(info.Length, info.LastWriteTimeUtc));
                }
            }
        return result;
    }

    private static void AssertCacheUnchanged(string root, Dictionary<string, FileStamp> before)
    {
        var after = CacheFiles(root);
        Require(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var stamp) && stamp == pair.Value),
            "cache_file_residue_or_change");
    }

    private static void EnsureOwnedAudio(string root, string path)
    {
        string full = Path.GetFullPath(path), cache = Path.Combine(root, "cache");
        Require(string.Equals(Path.GetDirectoryName(full), cache, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("voice-", StringComparison.Ordinal)
            && Path.GetExtension(full).Equals(".wav", StringComparison.OrdinalIgnoreCase), "audio_outside_owned_cache");
        RejectLinks(full);
    }
    private static void DeleteAudio(string root, string path, HashSet<string> owned)
    {
        EnsureOwnedAudio(root, path);
        File.Delete(path);
        owned.Remove(path);
    }

    private static WaveInfo ValidateWave(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        Require(stream.Length is >= 44 and <= 32 * 1024 * 1024, "wav_size_invalid");
        Require(reader.ReadUInt32() == 0x46464952, "wav_riff_missing");
        uint riffBytes = reader.ReadUInt32();
        Require((long)riffBytes + 8 == stream.Length && reader.ReadUInt32() == 0x45564157, "wav_header_invalid");
        ushort format = 0, channels = 0, bits = 0, alignment = 0;
        uint rate = 0, bytesPerSecond = 0;
        long samples = 0;
        double sumSquares = 0, peak = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            uint kind = reader.ReadUInt32(), size = reader.ReadUInt32();
            long end = checked(stream.Position + size);
            Require(end <= stream.Length, "wav_chunk_truncated");
            if (kind == 0x20746d66)
            {
                Require(size >= 16, "wav_fmt_truncated");
                format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadUInt32();
                bytesPerSecond = reader.ReadUInt32(); alignment = reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            else if (kind == 0x61746164)
            {
                Require(format == 1 && bits == 16 && channels is >= 1 and <= 2 && size % (channels * 2) == 0,
                    "wav_expected_pcm16");
                while (stream.Position < end)
                {
                    double sample = reader.ReadInt16() / 32768.0;
                    peak = Math.Max(peak, Math.Abs(sample)); sumSquares += sample * sample; samples++;
                }
            }
            stream.Position = end + (size & 1);
        }
        Require(rate is >= 8000 and <= 192000 && alignment == channels * 2 && bytesPerSecond == rate * alignment,
            "wav_format_invalid");
        double seconds = (double)samples / channels / rate;
        Require(seconds is >= 0.1 and <= 120 && peak > 0 && sumSquares > 0, "wav_empty_silent_or_invalid_duration");
        return new(rate, samples / channels, Math.Round(seconds, 3), Math.Round(peak, 6), Math.Round(Math.Sqrt(sumSquares / samples), 6));
    }

    private static byte[] HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return SHA256.HashData(file);
    }
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "reparse_path_rejected");
    }
    private static void DisposeHandles(Dictionary<int, Process> workers)
    {
        foreach (var process in workers.Values) process.Dispose();
        workers.Clear();
    }
    private static void Require(bool condition, string code) { if (!condition) throw new SmokeFailure(code); }
    private static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    private sealed class SmokeFailure(string code) : Exception { public string Code { get; } = code; }
    private sealed record FileStamp(long Length, DateTime ModifiedUtc);
    private sealed record WaveInfo(uint SampleRate, long Frames, double DurationSeconds, double Peak, double Rms);
}
