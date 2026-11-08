using System.Net;
using System.Text;
using System.Text.Json;
using Qiban.Core;

internal static class MemoryLearningNetworkTests
{
    private const string Proposal = """
        {"Memories":[{"Content":"用户喜欢乌龙茶","Category":"偏好","Confidence":0.96,"SourceMessageId":"user-tea","Evidence":"烏龍茶が好きです","ConflictsWithIds":[]}],"Summary":[{"Text":"用户谈到喜欢乌龙茶。","SourceMessageId":"user-tea","Evidence":"烏龍茶が好きです"}]}
        """;

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Automatic learning uses the configured model and separates records from instructions", async () =>
        {
            int calls = 0;
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                calls++;
                Check(request.RequestUri!.AbsolutePath == "/v1/chat/completions", "Unexpected learning endpoint");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = body.RootElement;
                Check(root.GetProperty("model").GetString() == "memory-fixture" && !root.GetProperty("stream").GetBoolean()
                    && root.GetProperty("max_tokens").GetInt32() == 4096, "Learning request does not use a bounded compatible model call");
                var messages = root.GetProperty("messages");
                Check(messages.GetArrayLength() == 2 && messages[0].GetProperty("role").GetString() == "system"
                    && messages[0].GetProperty("content").GetString()!.Contains("对话记忆整理器"), "Analysis is not separated from roleplay");
                using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                Check(data.RootElement.GetProperty("messages")[0].GetProperty("Id").GetString() == "user-tea"
                    && data.RootElement.GetProperty("messages")[0].GetProperty("Text").GetString() == "烏龍茶が好きです"
                    && data.RootElement.GetProperty("enableMemories").GetBoolean()
                    && data.RootElement.GetProperty("newUserMessageIds")[0].GetString() == "user-tea", "Source records or feature flags missing");
                return Reply(Proposal);
            }));
            var result = await client.AnalyzeMemoryAsync(Settings(), Input(), default);
            Check(calls == 1 && result.Memories.Single().Evidence == "烏龍茶が好きです"
                && result.Summary.Single().SourceMessageId == "user-tea", "Cross-language evidence was changed or request retried");
        });
        await test("Learning accepts fenced final JSON after completed reasoning and ignores hidden reasoning fields", async () =>
        {
            foreach (string final in new[] { Proposal, "```json\n" + Proposal + "\n```", "<think>Invalid draft {\"Memories\":[]}.</think>\n" + Proposal,
                "<analysis>非最终结果。</analysis>\n" + Proposal })
            {
                using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply(final, reasoning: "not-final-secret-fixture"))));
                var result = await client.AnalyzeMemoryAsync(Settings(), Input(), default);
                Check(result.Memories.Single().Content == "用户喜欢乌龙茶" && result.Summary.Count == 1, "Final learning JSON was not isolated");
            }
        });
        await test("Learning refuses malformed, ambiguous, duplicated, unfinished and oversized proposals", async () =>
        {
            foreach (string text in new[]
            {
                "<think>" + Proposal, Proposal + Proposal, "[" + Proposal + "]", "{\"Memories\":[]}",
                "{\"Memories\":[],\"memories\":[],\"Summary\":[]}", "{\"Memories\":[],\"Summary\":[],\"command\":\"execute\"}",
                Proposal.Replace("0.96", "2"), Proposal.Replace("偏好", "猜测"),
                Proposal.Replace("烏龍茶が好きです", new string('茶', 2001)), new string('x', 64001),
                "```xml\n" + Proposal + "\n```", "<analysis>done</analysis><think>again</think>" + Proposal
            })
            {
                using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply(text))));
                await Reject<InvalidDataException>(() => client.AnalyzeMemoryAsync(Settings(), Input(), default));
            }
        });
        await test("Learning rejects content filtered or truncated completions without committing a partial result", async () =>
        {
            foreach (string reason in new[] { "length", "content_filter" })
            {
                using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply(Proposal, reason))));
                await Reject<InvalidDataException>(() => client.AnalyzeMemoryAsync(Settings(), Input(), default));
            }
        });
        await test("Learning supports servers that stream despite stream=false with split Unicode events", async () =>
        {
            string half1 = Proposal[..(Proposal.Length / 2)], half2 = Proposal[(Proposal.Length / 2)..];
            string events = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = half1 } } } }) + "\r\n\r\n"
                + "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = half2 }, finish_reason = "stop" } } }) + "\n\n"
                + "data: [DONE]\n\n";
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Response(events, "text/event-stream"))));
            var result = await client.AnalyzeMemoryAsync(Settings(), Input(), default);
            Check(result.Memories.Single().Evidence == "烏龍茶が好きです" && result.Summary.Count == 1, "Streaming proposal was damaged");
        });
        await test("Learning rejects prematurely closed streams even when the partial body contains parseable JSON", async () =>
        {
            string events = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Proposal } } } }) + "\n\n";
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Response(events, "text/event-stream"))));
            await Reject<IOException>(() => client.AnalyzeMemoryAsync(Settings(), Input(), default));
        });
        await test("Disabling learning prevents calls and independent feature flags suppress unsolicited result sections", async () =>
        {
            int calls = 0;
            using var client = new ChatClient(new FakeHandler((_, _) => { calls++; return Task.FromResult(Reply(Proposal)); }));
            var settings = Settings(); settings.AutoMemoryEnabled = false; settings.AutoSummaryEnabled = false;
            var empty = await client.AnalyzeMemoryAsync(settings, Input(), default);
            Check(calls == 0 && empty.Memories.Count == 0 && empty.Summary.Count == 0, "Disabled learning contacted the provider");
            settings.AutoSummaryEnabled = true;
            var summaries = await client.AnalyzeMemoryAsync(settings, Input(), default);
            Check(summaries.Memories.Count == 0 && summaries.Summary.Count == 1, "Disabled memory proposals escaped filtering");
            settings.AutoMemoryEnabled = true; settings.AutoSummaryEnabled = false;
            var memories = await client.AnalyzeMemoryAsync(settings, Input(), default);
            Check(memories.Memories.Count == 1 && memories.Summary.Count == 0 && calls == 2, "Disabled summary escaped filtering");
        });
        await test("Cancelling automatic analysis cancels its network request and does not retry it", async () =>
        {
            int calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new ChatClient(new FakeHandler(async (_, ct) =>
            {
                calls++; entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Reply(Proposal);
            }));
            using var cancellation = new CancellationTokenSource();
            var pending = client.AnalyzeMemoryAsync(Settings(), Input(), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await Reject<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(calls == 1, "Cancelled analysis was retried");
        });
        await test("Learning does not reveal service response bodies on authentication failures", async () =>
        {
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("credential-body-fixture") })));
            try { await client.AnalyzeMemoryAsync(Settings(), Input(), default); throw new Exception("Expected HTTP rejection"); }
            catch (IOException error) { Check(!error.Message.Contains("credential-body-fixture") && error.Message.Contains("401"), "Server error body leaked"); }
        });
        await test("Source-checked rolling summaries survive short recent context and reach normal and proactive chat", async () =>
        {
            var friend = new FriendProfile
            {
                Persona = "自定义角色",
                Messages = [new() { Id = "user-tea", Role = "user", Text = "烏龍茶が好きです" },
                    new() { Id = "assistant-plan", Role = "assistant", Text = "以后可以一起聊茶。" },
                    new() { Id = "user-now", Role = "user", Text = "今天聊什么？" }]
            };
            friend.Learning.Summary = [new() { Text = "用户谈到喜欢乌龙茶。", SourceMessageId = "user-tea", Evidence = "烏龍茶が好きです", Role = "user" },
                new() { Text = "角色建议以后一起聊茶，尚未发生。", SourceMessageId = "assistant-plan", Evidence = "以后可以一起聊茶。", Role = "assistant" },
                new() { Text = "不应出现的丢失来源", SourceMessageId = "missing", Evidence = "unknown", Role = "user" }];
            var settings = Settings(); settings.ContextMessages = 1;
            int requests = 0;
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                requests++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                string summary = messages.Select(m => m.GetProperty("content").GetString()!)
                    .Single(text => text.StartsWith("以下是模型整理且保留原文来源"));
                Check(summary.Contains("不能当作用户事实或已发生事件") && !summary.Contains("不应出现的丢失来源"), "Summary role boundaries or source filter missing");
                using var data = JsonDocument.Parse(summary[(summary.IndexOf('\n') + 1)..]);
                Check(data.RootElement.GetArrayLength() == 2 && data.RootElement[0].GetProperty("sourceMessageId").GetString() == "user-tea"
                    && data.RootElement[1].GetProperty("role").GetString() == "assistant"
                    && !data.RootElement[0].TryGetProperty("Evidence", out _), "Summary source attribution was lost or evidence inflated the prompt");
                Check(messages.Count(m => m.GetProperty("role").GetString() == "user") == 1, "Recent context message budget was bypassed");
                return Reply("聊一聊乌龙茶吧。");
            }));
            await client.StreamAsync(settings, friend, [], _ => { }, default);
            await client.StreamInitiativeAsync(settings, friend, [], new InitiativeTrigger("idle", null, 20), _ => { }, default);
            Check(requests == 2, "Both chat paths did not use learned summary");
        });
        await test("Removing or suppressing summary sources removes them from later model prompts", async () =>
        {
            var friend = new FriendProfile();
            friend.Messages.Add(new() { Id = "source", Text = "旧的偏好" });
            friend.Learning.Summary.Add(new() { Text = "被删除的旧偏好", SourceMessageId = "source", Evidence = "旧的偏好", Role = "user" });
            friend.Learning.SuppressedSourceIds.Add("source");
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                string body = await request.Content!.ReadAsStringAsync(ct);
                Check(!body.Contains("被删除的旧偏好"), "Suppressed summary leaked into model context");
                return Reply("你好");
            }));
            await client.StreamAsync(Settings(), friend, [], _ => { }, default);
            friend.Messages.Clear(); friend.Learning.SuppressedSourceIds.Clear();
            await client.StreamAsync(Settings(), friend, [], _ => { }, default);
        });
    }

    private static AppSettings Settings() => new() { Model = "memory-fixture", Endpoint = "http://127.0.0.1:1234/v1" };
    private static MemoryLearningRequest Input() => new("default", "user-tea", [new("user-tea", "user", "烏龍茶が好きです")], [], [], true, true)
        { NewUserMessageIds = ["user-tea"] };
    private static HttpResponseMessage Reply(string text, string finish = "stop", string reasoning = "")
        => Response(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text, reasoning_content = reasoning }, finish_reason = finish } } }));
    private static HttpResponseMessage Response(string text, string type = "application/json") => new(HttpStatusCode.OK)
    { Content = new StreamContent(new SmallStream(Encoding.UTF8.GetBytes(text))) { Headers = { ContentType = new(type) } } };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
