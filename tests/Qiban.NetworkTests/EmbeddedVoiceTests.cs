using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Qiban.Core;

internal static class EmbeddedVoiceTests
{
    internal static async Task<bool> TryRunWorkerAsync(string[] args)
    {
        if (args.Contains("--embedded-voice-child")) { await Task.Delay(Timeout.Infinite); return true; }
        if (args.Contains("--embedded-voice-owner"))
        {
            string root = args[^1];
            using var client = CreateClient(root);
            var settings = Settings(root); settings.ReferenceText = "hang-with-child";
            await client.SynthesizeAsync(settings, "owner-lifetime", Path.Combine(root, "cache"), default);
            return true;
        }
        if (!args.Contains("--embedded-voice-fixture")) return false;
        Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);
        string work = args[^1];
        string? init = await Console.In.ReadLineAsync();
        if (init == null || !init.Contains("initialize")) return true;
        await Console.Out.WriteLineAsync("{\"id\":\"ready\",\"ok\":true}");
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            using var request = JsonDocument.Parse(line);
            var value = request.RootElement;
            string id = value.GetProperty("id").GetString()!;
            string mode = value.GetProperty("referenceText").GetString()!;
            string? signal = Environment.GetEnvironmentVariable("VOICE_FIXTURE_SIGNAL");
            if (signal != null)
                await WriteSignal(signal, JsonSerializer.Serialize(new { pid = Environment.ProcessId, work, request = value,
                    environment = new[] { "TEMP", "TMP", "APPDATA", "LOCALAPPDATA", "HOME", "USERPROFILE", "HF_HOME", "CUDA_CACHE_PATH", "TRITON_CACHE_DIR", "PYTHONUSERBASE", "NLTK_DATA" }
                        .ToDictionary(key => key, key => Environment.GetEnvironmentVariable(key)) }));
            if (mode is "hang" or "hang-with-child")
            {
                if (mode == "hang-with-child")
                {
                    var childStart = Self("--embedded-voice-child");
                    using var child = Process.Start(childStart)!;
                    if (signal != null) await WriteSignal(signal + ".child", child.Id.ToString());
                }
                await File.WriteAllTextAsync(Path.Combine(work, "unfinished.part"), "temporary");
                await Task.Delay(Timeout.Infinite);
            }
            if (mode == "crash") Environment.Exit(42);
            if (mode == "out-of-memory")
            {
                await Console.Error.WriteLineAsync("DO-NOT-EXPOSE-PRIVATE-TEXT");
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = false, code = "out_of_memory", error = "DO-NOT-EXPOSE-PRIVATE-TEXT" }));
                continue;
            }
            string? outputPath = value.GetProperty("outputPath").GetString();
            if (outputPath != null)
            {
                await File.WriteAllBytesAsync(outputPath, mode == "bad-wave" ? [1, 2, 3, 4] : Wave());
                if (mode == "wrong-path") outputPath += ".unexpected";
            }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id, ok = true, outputPath }));
        }
        return true;
    }

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string cache)
    {
        string root = Path.Combine(cache, "embedded-engine"); Directory.CreateDirectory(root);
        string signal = Path.Combine(root, "signal.json");
        try
        {
            await test("Embedded engine loads and synthesizes through a private process without HTTP or a reference for load", async () =>
            {
                using var client = CreateClient(root);
                var settings = Settings(root);
                string reference = settings.ReferenceAudio; settings.ReferenceAudio = "";
                await client.MountAsync(settings, default);
                int pid = ReadSignal(signal).GetProperty("pid").GetInt32();
                settings.ReferenceAudio = reference;
                string audio = await client.SynthesizeAsync(settings, "こんにちは。", Path.Combine(root, "cache"), default, japanese: true);
                var observation = ReadSignal(signal);
                Check(client.IsEmbeddedRunning && observation.GetProperty("pid").GetInt32() == pid, "Worker not reused");
                Check(observation.GetProperty("request").GetProperty("japanese").GetBoolean()
                    && observation.GetProperty("request").GetProperty("language").GetString() == "zh", "Language contract changed");
                Check(File.ReadAllBytes(audio).SequenceEqual(Wave()) && !audio.Contains("voice-engine"), "Audio was not detached from worker cache");
                string work = observation.GetProperty("work").GetString()!;
                foreach (var variable in observation.GetProperty("environment").EnumerateObject())
                    Check(variable.Value.GetString()!.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Environment escaped owned session");
                client.StopEmbedded();
                Check(!client.IsEmbeddedRunning && !Directory.Exists(work) && File.Exists(audio), "Stop lost playable audio or left worker files");
                File.Delete(audio);
            });
            await test("Stopping during embedded audio publication waits for a detached, playable WAV", async () =>
            {
                var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim();
                using var client = CreateClient(root, () =>
                {
                    publishing.TrySetResult();
                    Check(release.Wait(TimeSpan.FromSeconds(15)), "Audio publication test stalled");
                });
                var pending = client.SynthesizeAsync(Settings(root), "publication fixture", Path.Combine(root, "cache"), default);
                Task? stop = null;
                try
                {
                    await publishing.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    stop = Task.Run(() => { stopping.TrySetResult(); client.StopEmbedded(); });
                    await stopping.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.Delay(100);
                    Check(!stop.IsCompleted, "Stop removed the session during audio publication");
                }
                finally { release.Set(); }
                string audio = await pending.WaitAsync(TimeSpan.FromSeconds(15));
                await stop!.WaitAsync(TimeSpan.FromSeconds(15));
                Check(File.ReadAllBytes(audio).SequenceEqual(Wave()) && !client.IsEmbeddedRunning,
                    "Concurrent stop lost the published WAV");
                Check(!Directory.Exists(ReadSignal(signal).GetProperty("work").GetString()), "Concurrent stop left its session");
                File.Delete(audio);
            });
            await test("Cancellation at embedded audio publication leaves neither session nor playable cache files", async () =>
            {
                var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim();
                using var cancel = new CancellationTokenSource();
                using var client = CreateClient(root, () =>
                {
                    publishing.TrySetResult();
                    Check(release.Wait(TimeSpan.FromSeconds(15)), "Audio cancellation test stalled");
                });
                var pending = client.SynthesizeAsync(Settings(root), "publication cancellation", Path.Combine(root, "cache"), cancel.Token);
                Task? cancelling = null;
                try
                {
                    await publishing.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    cancelling = Task.Run(cancel.Cancel);
                    await Until(() => cancel.IsCancellationRequested);
                }
                finally { release.Set(); }
                await Expect<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(15)));
                await cancelling!.WaitAsync(TimeSpan.FromSeconds(15));
                Check(!client.IsEmbeddedRunning && !Directory.Exists(ReadSignal(signal).GetProperty("work").GetString()),
                    "Cancelled publication retained its worker session");
                Check(!Directory.EnumerateFiles(Path.Combine(root, "cache"), "voice-*.wav").Any(),
                    "Cancelled publication left detached audio");
            });
            await test("Embedded character weight pairs are snapshotted and passed independently", async () =>
            {
                using var client = CreateClient(root);
                var first = Settings(root);
                var second = Settings(root, "second");
                await client.MountAsync(first, default);
                await client.MountAsync(second, default);
                var request = ReadSignal(signal).GetProperty("request");
                Check(request.GetProperty("gptWeights").GetString() == second.GptWeights
                    && request.GetProperty("sovitsWeights").GetString() == second.SovitsWeights, "Pair mixed across characters");
            });
            await test("Embedded cancellation terminates its worker and child processes and removes partial audio", async () =>
            {
                using var client = CreateClient(root);
                var settings = Settings(root); settings.ReferenceText = "hang-with-child";
                if (File.Exists(signal + ".child")) File.Delete(signal + ".child");
                using var cancel = new CancellationTokenSource();
                var pending = client.SynthesizeAsync(settings, "cancel fixture", Path.Combine(root, "cache"), cancel.Token);
                await Until(() => File.Exists(signal + ".child"));
                var observed = ReadSignal(signal);
                int pid = observed.GetProperty("pid").GetInt32(), childId = int.Parse(File.ReadAllText(signal + ".child"));
                string work = observed.GetProperty("work").GetString()!;
                cancel.Cancel();
                await Expect<OperationCanceledException>(() => pending);
                await Until(() => Exited(pid) && Exited(childId));
                Check(!Directory.Exists(work) && !client.IsEmbeddedRunning, "Cancelled session leaked");
                settings.ReferenceText = "";
                await client.MountAsync(settings, default);
                Check(client.IsEmbeddedRunning && ReadSignal(signal).GetProperty("pid").GetInt32() != pid, "Cancelled worker was reused");
            });
            await test("Embedded failures redact diagnostics, clean the failed pipeline and allow a fresh retry", async () =>
            {
                using var client = CreateClient(root);
                var settings = Settings(root); settings.ReferenceText = "out-of-memory";
                try { await client.MountAsync(settings, default); throw new Exception("Expected engine failure"); }
                catch (IOException error) { Check(error.Message.Contains("显存") && !error.Message.Contains("PRIVATE"), "Error redaction or classification failed"); }
                Check(!client.IsEmbeddedRunning && !Directory.Exists(ReadSignal(signal).GetProperty("work").GetString()), "Failed process retained");
                settings.ReferenceText = "";
                await client.MountAsync(settings, default);
                Check(client.IsEmbeddedRunning, "Recovery did not start a fresh worker");
            });
            await test("Embedded invalid output paths and malformed WAV files cannot escape the owned session", async () =>
            {
                using var client = CreateClient(root);
                var settings = Settings(root);
                foreach (string mode in new[] { "wrong-path", "bad-wave", "crash" })
                {
                    settings.ReferenceText = mode;
                    await Expect<IOException>(() => client.SynthesizeAsync(settings, "output fixture", Path.Combine(root, "cache"), default));
                    Check(!client.IsEmbeddedRunning && !Directory.Exists(ReadSignal(signal).GetProperty("work").GetString()), "Failed output leaked a session");
                }
            });
            await test("Embedded missing weights and incomplete runtime fail before starting a process", async () =>
            {
                using var client = CreateClient(root);
                var settings = Settings(root); settings.GptWeights = "missing.ckpt";
                await Expect<InvalidDataException>(() => client.MountAsync(settings, default));
                Check(!client.IsEmbeddedRunning, "Invalid weights launched a worker");
                using var production = new VoiceClient(new RejectHttp(), root);
                await Expect<IOException>(() => production.MountAsync(Settings(root), default));
                Check(!production.IsEmbeddedRunning, "Incomplete runtime launched a worker");
            });
            await test("Disposing embedded voice cancels an active generation and releases its process files", async () =>
            {
                using var client = CreateClient(root);
                var settings = Settings(root); settings.ReferenceText = "hang";
                File.Delete(signal);
                var pending = client.SynthesizeAsync(settings, "dispose fixture", Path.Combine(root, "cache"), default);
                await Until(() => File.Exists(signal));
                var observed = ReadSignal(signal); int pid = observed.GetProperty("pid").GetInt32();
                client.Dispose();
                await Expect<OperationCanceledException>(() => pending);
                Check(Exited(pid) && !Directory.Exists(observed.GetProperty("work").GetString()), "Dispose left its worker or cache");
            });
            string normalSessions = Path.Combine(root, "cache", "voice-engine");
            Check(!Directory.Exists(normalSessions) || !Directory.EnumerateFileSystemEntries(normalSessions).Any(),
                "Normal disposal left an inference session before fixture cleanup");
            if (OperatingSystem.IsWindows())
                await test("Windows job ownership terminates the inference tree after the application process is killed", async () =>
                {
                    string isolated = Path.Combine(root, "owner-crash"); Directory.CreateDirectory(isolated);
                    string isolatedSignal = Path.Combine(isolated, "signal.json");
                    var start = Self("--embedded-voice-owner", isolated);
                    using var owner = Process.Start(start)!;
                    try
                    {
                        await Until(() => File.Exists(isolatedSignal + ".child"));
                        int pid = ReadSignal(isolatedSignal).GetProperty("pid").GetInt32();
                        int child = int.Parse(File.ReadAllText(isolatedSignal + ".child"));
                        owner.Kill(); await owner.WaitForExitAsync();
                        await Until(() => Exited(pid) && Exited(child));
                    }
                    finally { if (!owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync(); } }
                });
        }
        finally
        {
            // Fixtures (including intentional crash leftovers) are not application-owned user data.
            string owned = Path.GetFullPath(root);
            string boundary = Path.GetFullPath(cache).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(owned.StartsWith(boundary, StringComparison.OrdinalIgnoreCase), "Fixture cleanup escaped its test root");
            if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        }
    }

    private static VoiceClient CreateClient(string root, Action? beforePublish = null) => new(new RejectHttp(), root, (_, work) =>
    {
        var start = Self("--embedded-voice-fixture", work);
        start.Environment["VOICE_FIXTURE_SIGNAL"] = Path.Combine(root, "signal.json");
        return start;
    }, beforePublish);
    private static ProcessStartInfo Self(params string[] args)
    {
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    private static VoiceSettings Settings(string root, string name = "first")
    {
        string gpt = Path.Combine(root, name + ".ckpt"), sovits = Path.Combine(root, name + ".pth"), reference = Path.Combine(root, name + ".wav");
        File.WriteAllText(gpt, "fixture"); File.WriteAllText(sovits, "fixture"); File.WriteAllBytes(reference, Wave());
        return new() { Provider = "GPT-SoVITS 内置", GptWeights = gpt, SovitsWeights = sovits, ReferenceAudio = reference, Language = "zh" };
    }
    private static JsonElement ReadSignal(string path) { using var json = JsonDocument.Parse(File.ReadAllText(path)); return json.RootElement.Clone(); }
    private static async Task WriteSignal(string path, string value) { await File.WriteAllTextAsync(path + ".tmp", value); File.Move(path + ".tmp", path, true); }
    private static bool Exited(int pid) { try { using var process = Process.GetProcessById(pid); return process.HasExited; } catch (ArgumentException) { return true; } }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Expect<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed class RejectHttp : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Embedded voice attempted HTTP"); }
    private static byte[] Wave()
    {
        byte[] bytes = new byte[48]; "RIFF"u8.CopyTo(bytes); BitConverter.GetBytes(40).CopyTo(bytes, 4); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(bytes, 16); BitConverter.GetBytes((short)1).CopyTo(bytes, 20); BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(16000).CopyTo(bytes, 24); BitConverter.GetBytes(32000).CopyTo(bytes, 28); BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34); "data"u8.CopyTo(bytes.AsSpan(36)); BitConverter.GetBytes(4).CopyTo(bytes, 40); return bytes;
    }
}
