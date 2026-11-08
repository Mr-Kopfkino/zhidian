using System.Net;
using System.Text;
using System.Text.Json;
using Qiban.Core;

if (await EmbeddedVoiceTests.TryRunWorkerAsync(args)) return;

int passed = 0;
async Task Test(string name, Func<Task> action)
{
    await action(); passed++; Console.WriteLine("PASS " + name);
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
AppSettings Settings() => new() { Model = "test-local", Endpoint = "http://127.0.0.1:1234/v1" };
FriendProfile Friend() => new()
{
    Persona = "像古典文学人物一样说话。",
    Messages = [new() { Role = "user", Text = "你好" }, new() { Role = "assistant", Text = "failed-response", Status = "failed" },
        new() { Role = "assistant", Text = "cancelled-response", Status = "cancelled" }, new() { Role = "user", Text = "今天呢？" }]
};
HttpResponseMessage Response(string body, string type = "application/json") => new(HttpStatusCode.OK)
{ Content = new StreamContent(new SmallStream(Encoding.UTF8.GetBytes(body))) { Headers = { ContentType = new(type) } } };
byte[] Wave()
{
    byte[] data = new byte[48]; "RIFF"u8.CopyTo(data); BitConverter.GetBytes(40).CopyTo(data, 4); "WAVEfmt "u8.CopyTo(data.AsSpan(8));
    BitConverter.GetBytes(16).CopyTo(data, 16); BitConverter.GetBytes((short)1).CopyTo(data, 20); BitConverter.GetBytes((short)1).CopyTo(data, 22);
    BitConverter.GetBytes(16000).CopyTo(data, 24); BitConverter.GetBytes(32000).CopyTo(data, 28); BitConverter.GetBytes((short)2).CopyTo(data, 32);
    BitConverter.GetBytes((short)16).CopyTo(data, 34); "data"u8.CopyTo(data.AsSpan(36)); BitConverter.GetBytes(4).CopyTo(data, 40); return data;
}
string cache = Path.Combine(AppContext.BaseDirectory, "network-test-cache-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cache);
try
{
    await Test("SSE split UTF-8 / CRLF, context exclusions and memory provenance", async () =>
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "Wrong endpoint");
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = doc.RootElement.GetProperty("messages");
            Check(messages[0].GetProperty("content").GetString()!.Contains("古典文学"), "Persona missing");
            Check(!messages.ToString().Contains("failed-response") && !messages.ToString().Contains("cancelled-response"), "Failed assistant leaked");
            Check(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() == "今天呢？", "Current user missing");
            Check(messages.ToString().Contains("确认记忆") && !messages.ToString().Contains("未确认记忆"), "Memory state filter failed");
            return Response("data: {\"choices\":[{\"delta\":{\"content\":\"你好🌿\"}}]}\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"，朋友\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", "text/event-stream");
        });
        using var client = new ChatClient(handler); string received = "";
        await client.StreamAsync(Settings(), Friend(), [new() { State = "approved", Content = "确认记忆" }, new() { State = "pending", Content = "未确认记忆" }], s => received += s, default);
        Check(received == "你好🌿，朋友", "UTF-8 stream damaged");
    });
    await Test("Nonstream fallback and complete endpoint", async () =>
    {
        using var client = new ChatClient(new FakeHandler((request, _) =>
        {
            Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "Duplicated endpoint");
            return Task.FromResult(Response("{\"choices\":[{\"message\":{\"content\":\"完整回复\"}}]}"));
        }));
        var settings = Settings(); settings.Endpoint += "/chat/completions"; string text = "";
        await client.StreamAsync(settings, Friend(), [], s => text += s, default); Check(text == "完整回复", "Fallback failed");
    });
    await Test("MemoryEngine retrieval reaches actual chat payload", async () =>
    {
        var friend = Friend();
        friend.Memories = [new() { State = "approved", Content = "我喜欢桂花乌龙茶", SourceMessageId = "user-evidence" },
            new() { State = "pending", Content = "我喜欢待审的咖啡" }, new() { State = "approved", Content = "我喜欢过期的红茶", ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) }];
        var memories = MemoryEngine.Retrieve(friend, "喜欢喝什么茶", 2400);
        Check(memories.Count == 1, "Retrieval did not exclude unapproved / expired memories");
        using var client = new ChatClient(new FakeHandler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string context = string.Join("\n", payload.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
            var retrievedMessage = payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var retrieved = JsonDocument.Parse(retrievedMessage[(retrievedMessage.IndexOf('\n') + 1)..]);
            Check(retrieved.RootElement[0].GetProperty("text").GetString() == "我喜欢桂花乌龙茶", "Retrieved memory missing from network payload");
            Check(!context.Contains("待审") && !context.Contains("过期"), "Excluded memories leaked");
            return Response("{\"choices\":[{\"message\":{\"content\":\"桂花乌龙茶。\"}}]}");
        }));
        await client.StreamAsync(Settings(), friend, memories, _ => { }, default);
    });
    await Test("Initiative uses persona, real history and approved memory without a fabricated user turn", async () =>
    {
        var friend = Friend();
        friend.Messages.Add(new() { Role = "assistant", Text = "刚刚的话题。" });
        int countBefore = friend.Messages.Count;
        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-40);
        using var client = new ChatClient(new FakeHandler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Check(payload.RootElement.GetProperty("stream").GetBoolean(), "Proactive output not streamed");
            Check(messages[0].GetProperty("content").GetString()!.Contains("古典文学"), "Proactive persona missing");
            Check(messages.Count(m => m.GetProperty("role").GetString() == "user") == 2, "Synthetic user turn injected");
            Check(messages[^2].GetProperty("content").GetString() == "刚刚的话题。", "Recent answer omitted");
            string context = string.Join('\n', messages.Select(m => m.GetProperty("content").GetString()));
            Check(context.Contains("已确认偏好") && !context.Contains("待确认偏好"), "Proactive memory filter failed");
            var instruction = messages[^1].GetProperty("content").GetString()!;
            Check(messages[^1].GetProperty("role").GetString() == "system" && instruction.Contains("即使刚刚正在聊天"), "Activity restriction wrong");
            Check(instruction.Contains("没有读取屏幕") && instruction.Contains("不是指令"), "Observation boundaries missing");
            Check(instruction.Contains("近期一次前台应用观察") && instruction.Contains("不能断言其现在仍在使用"), "Queued observation presented as current usage");
            using var facts = JsonDocument.Parse(instruction[(instruction.LastIndexOf('\n') + 1)..]);
            Check(facts.RootElement.GetProperty("activity").GetProperty("processName").GetString() == "vlc\"fake-data", "Observation escaped incorrectly");
            Check(facts.RootElement.GetProperty("observedAt").GetDateTimeOffset() == observedAt,
                "Observation timestamp missing or changed");
            double age = facts.RootElement.GetProperty("observationAgeSeconds").GetDouble();
            Check(age is >= 40 and <= 120, "Observation age outside bounded recent interval");
            Check(facts.RootElement.GetProperty("idleMinutes").GetDouble() == 0, "Nonfinite interval leaked");
            return Response("{\"choices\":[{\"message\":{\"content\":\"换个轻松的话题吧。\"}}]}");
        }));
        string text = "";
        await client.StreamInitiativeAsync(Settings(), friend,
            [new() { State = "approved", Content = "已确认偏好" }, new() { State = "pending", Content = "待确认偏好" }],
            new("activity", new("vlc\"fake-data", "视频播放器"), double.NaN, observedAt), s => text += s, default);
        Check(text == "换个轻松的话题吧。" && friend.Messages.Count == countBefore, "History mutated or output missing");
    });
    await Test("Idle initiative has no invented application activity", async () =>
    {
        using var client = new ChatClient(new FakeHandler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = payload.RootElement.GetProperty("messages");
            string text = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString()!;
            using var facts = JsonDocument.Parse(text[(text.LastIndexOf('\n') + 1)..]);
            Check(facts.RootElement.GetProperty("trigger").GetString() == "idle" &&
                facts.RootElement.GetProperty("activity").ValueKind == JsonValueKind.Null, "Idle invented activity");
            Check(facts.RootElement.GetProperty("observedAt").ValueKind == JsonValueKind.Null &&
                facts.RootElement.GetProperty("observationAgeSeconds").ValueKind == JsonValueKind.Null, "Idle invented observation time");
            return Response("data: {\"choices\":[{\"delta\":{\"content\":\"在想什么？\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", "text/event-stream");
        }));
        string output = "";
        await client.StreamInitiativeAsync(Settings(), Friend(), [], new("idle", null, 8), s => output += s, default);
        Check(output == "在想什么？", "Idle stream failed");
    });
    await Test("Character cadence analysis uses only persona and parses fenced numeric JSON", async () =>
    {
        using var client = new ChatClient(new FakeHandler(async (request, ct) =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var messages = payload.RootElement.GetProperty("messages");
            Check(!payload.RootElement.GetProperty("stream").GetBoolean() && messages.GetArrayLength() == 2, "Wrong analysis request");
            using var persona = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Check(persona.RootElement.GetProperty("persona").GetString() == Friend().Persona, "Persona not supplied");
            Check(!persona.RootElement.TryGetProperty("Messages", out _) && !persona.RootElement.TryGetProperty("activity", out _), "Private context sent to analysis");
            string answer = "```json\n{\"IdleMinMinutes\":0.5,\"IdleMaxMinutes\":3,\"ActivityProbability\":0.65,\"ActivityCooldownMinutes\":1}\n```";
            return Response(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }));
        }));
        var profile = await client.GetInitiativeProfileAsync(Settings(), Friend(), default);
        Check(profile == new InitiativeProfile(.5, 3, .65, 1), "Analysis values not parsed");
    });
    await Test("Character cadence bounds reversed, extreme and case-insensitive numbers", async () =>
    {
        using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "{\"idleMinMinutes\":999999,\"idleMaxMinutes\":-3,\"activityProbability\":2,\"activityCooldownMinutes\":-1}" } } }
        })))));
        var profile = await client.GetInitiativeProfileAsync(Settings(), Friend(), default);
        Check(profile.IdleMinMinutes >= .5 && profile.IdleMaxMinutes <= 240 && profile.IdleMaxMinutes > profile.IdleMinMinutes,
            "Invalid cadence survived bounds");
        Check(profile.ActivityProbability == .8 && profile.ActivityCooldownMinutes == .5, "Activity bounds failed");
    });
    await Test("Malformed, missing, duplicate or nonfinite cadence never becomes a valid profile", async () =>
    {
        foreach (string value in new[] { "not JSON", "[]", "{}",
            "{\"IdleMinMinutes\":1,\"IdleMaxMinutes\":2,\"ActivityProbability\":1e999,\"ActivityCooldownMinutes\":2}",
            "{\"IdleMinMinutes\":1,\"idleMinMinutes\":2,\"IdleMaxMinutes\":3,\"ActivityProbability\":0.1,\"ActivityCooldownMinutes\":2}" })
        {
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { content = value } } } })))));
            await Throws<InvalidDataException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), default));
        }
    });
    await Test("Cadence cancellation propagates without a retry", async () =>
    {
        int calls = 0;
        using var client = new ChatClient(new FakeHandler(async (_, ct) =>
        { calls++; await Task.Delay(10_000, ct); return Response("{}"); }));
        using var cancel = new CancellationTokenSource(20);
        await Throws<OperationCanceledException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), cancel.Token));
        Check(calls == 1, "Cadence retried POST");
    });
    await Test("HTTP errors redact body and never retry", async () =>
    {
        int calls = 0;
        using var client = new ChatClient(new FakeHandler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-echo-sentinel") }); }));
        try { await client.StreamAsync(Settings(), Friend(), [], _ => { }, default); throw new Exception("Expected HTTP error"); }
        catch (IOException e) { Check(!e.Message.Contains("secret-echo-sentinel") && e.Message.Contains("401"), "Error leaked body"); }
        Check(calls == 1, "POST retried");
    });
    await Test("Truncated SSE marked incomplete", async () =>
    {
        using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Response("data: {\"choices\":[{\"delta\":{\"content\":\"部分\"}}]}\n\n", "text/event-stream"))));
        await Throws<IOException>(() => client.StreamAsync(Settings(), Friend(), [], _ => { }, default));
    });
    await Test("Oversized stream line rejected", async () =>
    {
        using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("data: " + new string('x', 66000), Encoding.UTF8, "text/event-stream") })));
        await Throws<InvalidDataException>(() => client.StreamAsync(Settings(), Friend(), [], _ => { }, default));
    });
    await Test("Models test does not submit chat", async () =>
    {
        using var client = new ChatClient(new FakeHandler((request, _) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/models", "Test sent chat");
            return Task.FromResult(Response("{\"data\":[{\"id\":\"test-local\"}]}"));
        }));
        Check((await client.TestAsync(Settings(), default)).Contains("已找到"), "Model not found");
    });
    await Test("Reply length stays within storage contract", async () =>
    {
        using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = new string('x', 200_001) } } } }), Encoding.UTF8, "application/json") })));
        await Throws<InvalidDataException>(() => client.StreamAsync(Settings(), Friend(), [], _ => throw new Exception("Oversized reply displayed"), default));
    });
    await Test("Remote HTTP blocked before transport", async () =>
    {
        using var client = new ChatClient(new FakeHandler((_, _) => throw new Exception("Should not send")));
        var settings = Settings(); settings.Endpoint = "http://example.invalid/v1";
        await Throws<InvalidDataException>(() => client.StreamAsync(settings, Friend(), [], _ => { }, default));
    });
    await Test("Cancellation propagates", async () =>
    {
        using var client = new ChatClient(new FakeHandler(async (_, ct) => { await Task.Delay(10_000, ct); return Response("{}"); }));
        using var cancel = new CancellationTokenSource(20);
        await Throws<OperationCanceledException>(() => client.StreamAsync(Settings(), Friend(), [], _ => { }, cancel.Token));
    });
    await Test("Custom voice nested JSON escaping and WAV delivery", async () =>
    {
        string spoken = "有\"引号\"\\路径\n中文🌿";
        using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(doc.RootElement.GetProperty("nested")[0].GetProperty("say").GetString() == "前" + spoken + "后", "JSON injection / replacement error");
            Check(doc.RootElement.GetProperty("keep").GetInt32() == 7, "Unrelated value changed");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
        }));
        var settings = new VoiceSettings { Provider = "Custom", RequestTemplate = "{\"nested\":[{\"say\":\"前{{text}}后\"}],\"keep\":7}" };
        string path = await client.SynthesizeAsync(settings, spoken, cache, default);
        Check(File.Exists(path) && path.EndsWith(".wav") && !Directory.GetFiles(cache, "*.part").Any(), "Audio finalization failed"); File.Delete(path);
    });
    await Test("Invalid audio cleans partial file", async () =>
    {
        using var client = new VoiceClient(new FakeHandler((_, _) => Task.FromResult(Response("{\"error\":\"not audio\"}"))));
        await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "Custom" }, "test", cache, default));
        Check(!Directory.GetFiles(cache).Any(), "Invalid response left garbage");
    });
    await Test("Audio size bounded before disk write", async () =>
    {
        using var client = new VoiceClient(new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            response.Content.Headers.ContentLength = 33 * 1024 * 1024; return Task.FromResult(response);
        }));
        await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "Custom" }, "test", cache, default));
        Check(!Directory.GetFiles(cache).Any(), "Oversized response left garbage");
    });
    await Test("Active audio locked and cancellation removes partial", async () =>
    {
        bool checkedLock = false;
        using var client = new VoiceClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream(Wave(), () =>
            {
                string partial = Directory.GetFiles(cache, "*.part").Single();
                try { using var opened = File.Open(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                catch (IOException) { checkedLock = true; }
            }))
        })));
        using var cancel = new CancellationTokenSource(100);
        await Throws<OperationCanceledException>(() => client.SynthesizeAsync(new() { Provider = "Custom JSON" }, "test", cache, cancel.Token));
        Check(checkedLock && !Directory.GetFiles(cache).Any(), "Active file unlocked or cancelled file retained");
    });
    await Test("GPT-SoVITS request and mount contracts", async () =>
    {
        int mounts = 0;
        using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Check(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("权重 路径"), "Path not encoded correctly");
                mounts++; return Response("{\"message\":\"success\"}");
            }
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Check(request.RequestUri!.AbsolutePath == "/tts", "Wrong TTS path");
            Check(doc.RootElement.GetProperty("text_lang").GetString() == "ja" && !doc.RootElement.GetProperty("streaming_mode").GetBoolean(), "Wrong GPT contract");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
        }));
        var settings = new VoiceSettings { Provider = "GPT-SoVITS 外接", ReferenceAudio = "ref.wav", Language = "ja", GptWeights = "D:/权重 路径/g.ckpt", SovitsWeights = "D:/权重 路径/s.pth" };
        await client.MountAsync(settings, default); Check(mounts == 2, "Mount missing");
        string path = await client.SynthesizeAsync(settings, "こんにちは", cache, default); File.Delete(path);
        Check(mounts == 4, "Synthesis did not restore its own weight pair");
    });
    await Test("Role-specific compatible voices snapshot queued settings", async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            received.Add(request.RequestUri!.Host + "/" + doc.RootElement.GetProperty("model").GetString()
                + "/" + doc.RootElement.GetProperty("voice").GetString());
            if (received.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
        }));
        var first = new VoiceSettings { Provider = "OpenAI-compatible", Endpoint = "https://first.invalid/v1/audio/speech", Model = "voice-model-a", Voice = "speaker-a" };
        var second = new VoiceSettings { Provider = "OpenAI-compatible", Endpoint = "https://second.invalid/v1/audio/speech", Model = "voice-model-b", Voice = "speaker-b" };
        var a = client.SynthesizeAsync(first, "first", cache, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = client.SynthesizeAsync(second, "second", cache, default);
        second.Endpoint = "https://changed.invalid/tts"; second.Model = "changed"; second.Voice = "changed";
        Check(received.Count == 1, "Queued voice bypassed the operation gate");
        release.SetResult();
        foreach (string path in await Task.WhenAll(a, b)) File.Delete(path);
        Check(received.SequenceEqual(new[] { "first.invalid/voice-model-a/speaker-a", "second.invalid/voice-model-b/speaker-b" }),
            "A role change mutated another role's queued request");
    });
    await Test("Shared GPT server restores A B A weights before each synthesis", async () =>
    {
        var calls = new List<string>();
        string currentGpt = "", currentSovits = "";
        using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                string path = Uri.UnescapeDataString(request.RequestUri!.Query["?weights_path=".Length..]);
                calls.Add(path);
                if (request.RequestUri.AbsolutePath == "/set_gpt_weights") currentGpt = path;
                else currentSovits = path;
                return Response("\"success\"");
            }
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            string role = doc.RootElement.GetProperty("text").GetString()!;
            Check(currentGpt == role + ".ckpt" && currentSovits == role + ".pth", "Shared service used the wrong role's pair");
            Check(doc.RootElement.GetProperty("ref_audio_path").GetString() == role + ".wav", "Reference audio mixed between roles");
            calls.Add("tts:" + role);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
        }));
        foreach (string role in new[] { "A", "B", "A" })
        {
            var settings = new VoiceSettings { Provider = "GPT-SoVITS 外接", ReferenceAudio = role + ".wav", GptWeights = role + ".ckpt", SovitsWeights = role + ".pth" };
            File.Delete(await client.SynthesizeAsync(settings, role, cache, default));
        }
        Check(calls.SequenceEqual(new[] { "A.ckpt", "A.pth", "tts:A", "B.ckpt", "B.pth", "tts:B", "A.ckpt", "A.pth", "tts:A" }),
            "Weight caching skipped or reordered a role transition");
    });
    await Test("Manual mount waits for complete audio download and queued cancellation is isolated", async () =>
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        using var client = new VoiceClient(new FakeHandler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.Method == HttpMethod.Get ? Response("\"success\"") :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedAudioStream(Wave(), reading, release.Task)) });
        }));
        var settings = new VoiceSettings { Provider = "GPT-SoVITS 外接", ReferenceAudio = "a.wav", GptWeights = "a.ckpt", SovitsWeights = "a.pth" };
        var speech = client.SynthesizeAsync(settings, "a", cache, default);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var queued = client.SynthesizeAsync(settings, "cancelled", cache, cancelled.Token);
        var manual = client.MountAsync(new() { Provider = "GPT-SoVITS 外接", GptWeights = "b.ckpt", SovitsWeights = "b.pth" }, default);
        Check(calls.SequenceEqual(new[] { "/set_gpt_weights", "/set_sovits_weights", "/tts" }), "Mount interleaved with active synthesis");
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => queued);
        Check(!manual.IsCompleted, "Queued cancellation released another operation's lease");
        release.SetResult();
        File.Delete(await speech);
        await manual.WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls.SequenceEqual(new[] { "/set_gpt_weights", "/set_sovits_weights", "/tts", "/set_gpt_weights", "/set_sovits_weights" })
            && !Directory.GetFiles(cache).Any(), "Lease was not released after audio completion");
    });
    await Test("Failed model switch prevents inference and subsequent role remounts a full pair", async () =>
    {
        bool fail = true; int gptMounts = 0, syntheses = 0;
        using var client = new VoiceClient(new FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/set_gpt_weights") { gptMounts++; return Task.FromResult(Response("\"success\"")); }
            if (request.RequestUri.AbsolutePath == "/set_sovits_weights")
            {
                if (fail) { fail = false; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); }
                return Task.FromResult(Response("\"success\""));
            }
            syntheses++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) });
        }));
        var settings = new VoiceSettings { Provider = "GPT-SoVITS 外接", ReferenceAudio = "a.wav", GptWeights = "a.ckpt", SovitsWeights = "a.pth" };
        await Throws<IOException>(() => client.SynthesizeAsync(settings, "failed", cache, default));
        Check(syntheses == 0 && !Directory.GetFiles(cache).Any(), "Failed mount generated audio or garbage");
        File.Delete(await client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", ReferenceAudio = "b.wav", GptWeights = "b.ckpt", SovitsWeights = "b.pth" }, "b", cache, default));
        Check(gptMounts == 2 && syntheses == 1, "Failure left the gate locked or skipped a full remount");
    });
    await Test("Role cancellation waits for in-flight server load before allowing the next role", async () =>
    {
        foreach (string pauseAt in new[] { "/set_gpt_weights", "/set_sovits_weights" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<string>();
            CancellationToken serverLoadToken = default;
            using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    string weight = Uri.UnescapeDataString(request.RequestUri!.Query["?weights_path=".Length..]);
                    calls.Add(weight);
                    if (request.RequestUri.AbsolutePath == pauseAt && weight.StartsWith("a.", StringComparison.Ordinal))
                    {
                        serverLoadToken = ct; entered.SetResult();
                        await release.Task.WaitAsync(ct); // The server load continues after the role is deselected.
                    }
                    return Response("\"success\"");
                }
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                calls.Add("tts:" + document.RootElement.GetProperty("text").GetString());
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
            }));
            using var cancel = new CancellationTokenSource();
            var first = client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", ReferenceAudio = "a.wav", GptWeights = "a.ckpt", SovitsWeights = "a.pth" }, "a", cache, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            var second = client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", ReferenceAudio = "b.wav", GptWeights = "b.ckpt", SovitsWeights = "b.pth" }, "b", cache, default);
            Check(!first.IsCompleted && !second.IsCompleted && !serverLoadToken.IsCancellationRequested
                && !calls.Any(call => call.StartsWith("b.", StringComparison.Ordinal)),
                "Role cancellation abandoned a server mutation or allowed another load to overlap it");
            release.SetResult();
            await Throws<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            File.Delete(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            string[] expected = pauseAt == "/set_gpt_weights"
                ? ["a.ckpt", "b.ckpt", "b.pth", "tts:b"]
                : ["a.ckpt", "a.pth", "b.ckpt", "b.pth", "tts:b"];
            Check(calls.SequenceEqual(expected) && !Directory.GetFiles(cache).Any(),
                "Cancelled role proceeded to another mount/inference, or its successor did not mount a complete pair");
        }
    });
    await Test("Incomplete or remote weight pairs reject before HTTP; dedicated preloaded service remains supported", async () =>
    {
        int calls = 0;
        using var client = new VoiceClient(new FakeHandler((request, _) =>
        {
            calls++; Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/tts", "Preloaded service was remounted");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) });
        }));
        await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", ReferenceAudio = "ref.wav", GptWeights = "only.ckpt" }, "a", cache, default));
        await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", ReferenceAudio = "ref.wav", SovitsWeights = "only.pth" }, "a", cache, default));
        await Throws<InvalidOperationException>(() => client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", Endpoint = "https://example.invalid/tts", ReferenceAudio = "ref.wav", GptWeights = "a.ckpt", SovitsWeights = "a.pth" }, "a", cache, default));
        Check(calls == 0, "Invalid weights contacted the service");
        File.Delete(await client.SynthesizeAsync(new() { Provider = "GPT-SoVITS 外接", Endpoint = "https://example.invalid/tts", ReferenceAudio = "ref.wav" }, "preloaded", cache, default));
        Check(calls == 1, "Preloaded dedicated endpoint did not synthesize");
    });
    await Test("Disposing voice client cancels active and waiting operations safely", async () =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new VoiceClient(new FakeHandler(async (_, ct) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Response("{}"); }));
        var first = client.SynthesizeAsync(new() { Provider = "Custom" }, "a", cache, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.SynthesizeAsync(new() { Provider = "Custom" }, "b", cache, default);
        client.Dispose(); client.Dispose();
        await Throws<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Throws<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(!Directory.GetFiles(cache).Any(), "Shutdown left partial audio");
    });
    await Test("Remote model mounting rejected", async () =>
    {
        using var client = new VoiceClient(new FakeHandler((_, _) => throw new Exception("Should not send")));
        await Throws<InvalidOperationException>(() => client.MountAsync(new() { Provider = "GPT-SoVITS 外接", Endpoint = "https://example.invalid/tts" }, default));
    });
    await JapaneseNetworkTests.RunAsync(cache, Test);
    await InitiativeProfileNetworkTests.RunAsync(Test);
    await MemoryLearningNetworkTests.RunAsync(Test);
    await EmbeddedVoiceTests.RunAsync(Test, cache);
    Console.WriteLine($"Network contracts: {passed} passed. No external service contacted.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error); Environment.ExitCode = 1;
}
finally
{
    foreach (var file in Directory.GetFiles(cache)) File.Delete(file);
    Directory.Delete(cache);
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
}
sealed class SmallStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
sealed class WaitingStream(byte[] bytes, Action firstRead) : MemoryStream(bytes)
{
    private bool read;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (read) { await Task.Delay(10_000, cancellationToken); return 0; }
        read = true; firstRead(); return await base.ReadAsync(buffer, cancellationToken);
    }
}
sealed class GatedAudioStream(byte[] bytes, TaskCompletionSource entered, Task release) : MemoryStream(bytes)
{
    private bool started;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!started) { started = true; entered.SetResult(); await release.WaitAsync(cancellationToken); }
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
