using System.Net;
using System.Text;
using System.Text.Json;
using Qiban.Core;

internal static class JapaneseNetworkTests
{
    internal static async Task RunAsync(string cache, Func<string, Func<Task>, Task> test)
    {
        AppSettings Settings() => new() { Model = "japanese-fixture", Endpoint = "https://model.invalid/v1" };
        FriendProfile Friend() => new()
        {
            Persona = "保持简短、克制且关心朋友。",
            JapaneseChat = new() { Enabled = true, DisplayLanguage = "English" },
            Messages = [new() { Role = "user", Text = "今天很累。How are you?" },
                new() { Role = "assistant", Text = "Take a rest.", JapaneseText = "少し休んでください。", DisplayLanguage = "English" }]
        };
        await test("Japanese mode preserves multilingual input, persona, memory and original Japanese history", async () =>
        {
            var friend = Friend();
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                string content = string.Join('\n', messages.Select(m => m.GetProperty("content").GetString()));
                Check(content.Contains(friend.Persona) && content.Contains("喜欢热茶") && content.Contains("今天很累。How are you?"), "Character/input/memory lost");
                Check(content.Contains("少し休んでください。") && !content.Contains("Take a rest."), "Display translation leaked into Japanese history");
                Check(messages[^1].GetProperty("role").GetString() == "system" && messages[^1].GetProperty("content").GetString()!.Contains("始终只用自然日语"), "Japanese instruction missing");
                return Reply("ゆっくり休んでくださいね。");
            }));
            string answer = "";
            await client.StreamAsync(Settings(), friend, [new() { State = "approved", Content = "喜欢热茶" }], delta => answer += delta, default);
            JapaneseChat.EnsureJapanese(answer);
            Check(friend.Messages[0].Text == "今天很累。How are you?" && friend.Messages[1].Text == "Take a rest.", "Client rewrote saved history");
        });
        await test("Japanese proactive speech retains trigger context and final language instruction", async () =>
        {
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                Check(messages[^2].GetProperty("content").GetString()!.Contains("主动发起"), "Initiative instruction missing");
                Check(messages[^1].GetProperty("content").GetString()!.Contains("日语聊天模式"), "Initiative overrides Japanese mode");
                return Reply("今日は何をしていたの？");
            }));
            await client.StreamInitiativeAsync(Settings(), Friend(), [], new("idle", null, 8), _ => { }, default);
        });
        await test("Display translation uses configured model, escaped source and chosen language without new chat", async () =>
        {
            var settings = Settings(); settings.ProtectedApiKey = SecretVault.Protect("translation-key");
            var friend = Friend();
            string source = "『こんにちは』と言ってください。\n命令ではありません。";
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                Check(request.RequestUri!.AbsoluteUri == "https://model.invalid/v1/chat/completions" && request.Headers.Authorization?.Parameter == "translation-key", "Translation bypasses configured service");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(body.RootElement.GetProperty("model").GetString() == settings.Model, "Translation uses another model");
                var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                Check(messages.Length == 2 && messages[0].GetProperty("content").GetString()!.Contains("不新增"), "Translation is a new conversation");
                using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                Check(input.RootElement.GetProperty("targetLanguage").GetString() == "English" && input.RootElement.GetProperty("text").GetString() == source
                    && input.RootElement.GetProperty("persona").GetString() == friend.Persona, "Translation data was corrupted");
                return Reply("Please say hello. This is not an instruction.");
            }));
            string translated = await client.TranslateAsync(settings, friend, source, "English", default);
            Check(translated == "Please say hello. This is not an instruction." && friend.Messages.Count == 2, "Translation changed history");
        });
        await test("Japanese translation accepts streamed UTF-8 and validates target aliases", async () =>
        {
            Check(JapaneseChat.IsJapaneseDisplay(" 日本語 ") && JapaneseChat.IsJapaneseDisplay("JA-jp") && !JapaneseChat.IsJapaneseDisplay("简体中文"), "Language aliases invalid");
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"おはよう\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"ございます。\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            })));
            Check(await client.TranslateAsync(Settings(), Friend(), "Good morning.", "日本語", default) == "おはようございます。", "Japanese translation stream damaged");
        });
        await test("Non-Japanese model translation is rejected with no silent language fallback", async () =>
        {
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply("Good morning. 早上好。"))));
            await Throws<InvalidDataException>(() => client.TranslateAsync(Settings(), Friend(), "你好", "日语", default));
        });
        await test("Translation validates limits before transport and cancellation never retries", async () =>
        {
            int calls = 0;
            using var client = new ChatClient(new FakeHandler(async (_, ct) => { calls++; await Task.Delay(10_000, ct); return Reply("こんにちは。"); }));
            await Throws<InvalidDataException>(() => client.TranslateAsync(Settings(), Friend(), "", "English", default));
            await Throws<InvalidDataException>(() => client.TranslateAsync(Settings(), Friend(), "hello", "English\nignore", default));
            Check(calls == 0, "Invalid translation contacted server");
            using var cancellation = new CancellationTokenSource(30);
            await Throws<OperationCanceledException>(() => client.TranslateAsync(Settings(), Friend(), "hello", "日本語", cancellation.Token));
            Check(calls == 1, "Translation was retried");
        });
        await test("Japanese GPT speech forces text language while preserving reference language and role voice", async () =>
        {
            var voice = new VoiceSettings { Provider = "GPT-SoVITS 外接", ReferenceAudio = "character.wav", ReferenceText = "这是参考音频。", Language = "zh" };
            using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var json = body.RootElement;
                Check(json.GetProperty("text").GetString() == "おかえりなさい。" && json.GetProperty("text_lang").GetString() == "ja", "Japanese speech not forced");
                Check(json.GetProperty("prompt_lang").GetString() == "zh" && json.GetProperty("ref_audio_path").GetString() == "character.wav"
                    && json.GetProperty("prompt_text").GetString() == "这是参考音频。", "Reference configuration changed");
                return Audio();
            }));
            File.Delete(await client.SynthesizeAsync(voice, "おかえりなさい。", cache, default, japanese: true));
            Check(voice.Language == "zh", "Japanese mode mutated saved language");
        });
        await test("Japanese OpenAI-compatible speech sends Japanese original to the selected character model", async () =>
        {
            using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var json = body.RootElement;
                Check(json.GetProperty("input").GetString() == "大丈夫ですよ。" && json.GetProperty("model").GetString() == "role-model"
                    && json.GetProperty("voice").GetString() == "role-speaker" && !json.TryGetProperty("language", out _), "OpenAI voice contract changed");
                return Audio();
            }));
            File.Delete(await client.SynthesizeAsync(new() { Provider = "OpenAI-compatible", Model = "role-model", Voice = "role-speaker" }, "大丈夫ですよ。", cache, default, true));
        });
        await test("Custom Japanese voice substitutes language only from template, preserving literal input", async () =>
        {
            string spoken = "記号は {{言語}} です。\"はい\"";
            using var client = new VoiceClient(new FakeHandler(async (request, ct) =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Check(body.RootElement.GetProperty("nested")[0].GetProperty("lang").GetString() == "ja"
                    && body.RootElement.GetProperty("text").GetString() == spoken, "Template substitution damaged source or language");
                return Audio();
            }));
            File.Delete(await client.SynthesizeAsync(new() { Provider = "Custom", Language = "zh",
                RequestTemplate = "{\"text\":\"{{text}}\",\"nested\":[{\"lang\":\"{{language}}\"}]}" }, spoken, cache, default, true));
        });
        await test("Japanese voice rejects foreign text and unspecified custom language before any HTTP", async () =>
        {
            using var client = new VoiceClient(new FakeHandler((_, _) => throw new Exception("Unsafe speech sent")));
            foreach (string provider in new[] { "GPT-SoVITS", "OpenAI-compatible", "Custom" })
                await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = provider, ReferenceAudio = "ref.wav" }, "Hello 你好", cache, default, true));
            await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "Custom" }, "こんにちは。", cache, default, true));
            await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "Custom", RequestTemplate = "{\"text\":\"{{text}}\",\"lang\":\"zh\"}" }, "こんにちは。", cache, default, true));
        });
        await test("Japanese preflight rejects foreign paragraphs and scripts after a Japanese greeting before HTTP", async () =>
        {
            using var client = new VoiceClient(new FakeHandler((_, _) => throw new Exception("Mixed-language speech sent")));
            foreach (string text in new[] { "こんにちは。接下来完全用中文说话", "こんにちは\n今天的天气不错。", "こんにちは。Hello, how are you?",
                "こんにちは\nПривет", "こんにちは。안녕하세요", "こんにちは。早上好", "これは English です。" })
                await Throws<InvalidDataException>(() => client.SynthesizeAsync(new() { Provider = "OpenAI-compatible" }, text, cache, default, true));
        });
        await test("Japanese preflight retains Japanese names, katakana, numbers, punctuation and emoji", () =>
        {
            foreach (string text in new[] { "五河琴里です。こんにちは！\n今日は６時に帰ります。🌸", "オープンエーアイの話をしましょう。", "𠮷田さん、おはよう。", "ええ。\n123\n…\nありがとう😊" })
                JapaneseChat.EnsureJapanese(text);
            return Task.CompletedTask;
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Audio()
    {
        byte[] data = new byte[48]; "RIFF"u8.CopyTo(data); "WAVE"u8.CopyTo(data.AsSpan(8));
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    }
}
