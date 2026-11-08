using System.Net;
using System.Text;
using System.Text.Json;
using Qiban.Core;

internal static class InitiativeProfileNetworkTests
{
    private const string Profile = "{\"IdleMinMinutes\":1,\"IdleMaxMinutes\":3,\"ActivityProbability\":0.45,\"ActivityCooldownMinutes\":2}";

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        AppSettings Settings() => new() { Model = "reasoning-fixture", Endpoint = "http://127.0.0.1:1234/v1" };
        FriendProfile Friend() => new() { Persona = "怕寂寞、喜欢分享，但不催促朋友。" };
        await test("Cadence analysis gives reasoning models a bounded output budget instead of 256 tokens", async () =>
        {
            int calls = 0;
            using var client = new ChatClient(new FakeHandler(async (request, ct) =>
            {
                calls++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                int limit = body.RootElement.GetProperty("max_tokens").GetInt32();
                Check(limit > 256 && limit <= 4096, "Reasoning output budget is too small or unbounded");
                Check(!body.RootElement.GetProperty("stream").GetBoolean(), "Profile request contract changed");
                return Reply(Profile);
            }));
            Check(await client.GetInitiativeProfileAsync(Settings(), Friend(), default) == new InitiativeProfile(1, 3, .45, 2)
                && calls == 1, "Profile changed or a submitted request was retried");
        });
        await test("Cadence accepts one final JSON object with prose, fences or completed reasoning wrappers", async () =>
        {
            foreach (string text in new[]
            {
                "分析结果如下：\n" + Profile + "\n这些参数符合该角色。",
                Profile + "\n以上为建议参数。",
                "建议：\n```json\n" + Profile + "\n```",
                "<think>候选值是 {\"IdleMinMinutes\":99}。" + new string('想', 9000) + "</think>\n```json\n" + Profile + "\n```",
                "<analysis>这里只做分析，不是最终参数。</analysis>\n" + Profile
            })
            {
                using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply(text))));
                Check(await client.GetInitiativeProfileAsync(Settings(), Friend(), default) == new InitiativeProfile(1, 3, .45, 2),
                    "Final profile was not parsed independently of the reasoning wrapper");
            }
        });
        await test("Cadence refuses unfinished reasoning, multiple candidates, arrays and nested substitutes", async () =>
        {
            foreach (string text in new[]
            {
                "<think>" + Profile,
                "<think>done</think>\n<think>" + Profile,
                "候选：" + Profile + "\n另一组：" + Profile,
                "[" + Profile + "]",
                "结果： [" + Profile + "]",
                "{\"profile\":" + Profile + "}",
                "结果：" + Profile + " {"
            })
            {
                using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply(text))));
                await Throws<InvalidDataException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), default));
            }
        });
        await test("Cadence rejects truncated or filtered responses even if their visible fragment resembles valid JSON", async () =>
        {
            foreach (string finish in new[] { "length", "content_filter" })
            {
                int calls = 0;
                using var client = new ChatClient(new FakeHandler((_, _) => { calls++; return Task.FromResult(Reply(Profile, finish)); }));
                await Throws<InvalidDataException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), default));
                Check(calls == 1, "Truncated analysis retried a submitted request");
            }
        });
        await test("Cadence never uses hidden reasoning as a final profile or fabricates values for an empty answer", async () =>
        {
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new
                    { message = new { content = (string?)null, reasoning_content = Profile }, finish_reason = "stop" } } }))
            })));
            await Throws<InvalidDataException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), default));
        });
        await test("Cadence handles a compatible service that returns SSE despite stream false", async () =>
        {
            string events = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = "分析" } } } })
                + "\n\ndata: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Profile }, finish_reason = "stop" } } })
                + "\n\ndata: [DONE]\n\n";
            using var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") })));
            Check(await client.GetInitiativeProfileAsync(Settings(), Friend(), default) == new InitiativeProfile(1, 3, .45, 2),
                "Compatible streamed analysis was rejected or hidden reasoning was used");
        });
        await test("Cadence keeps bounded output and does not accept an incomplete streamed profile", async () =>
        {
            using (var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(Reply(new string('x', 32_001))))))
                await Throws<InvalidDataException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), default));
            string events = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = Profile } } } }) + "\n\n";
            using (var client = new ChatClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") }))))
                await Throws<IOException>(() => client.GetInitiativeProfileAsync(Settings(), Friend(), default));
        });
    }

    private static HttpResponseMessage Reply(string text, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new
            { message = new { content = text }, finish_reason = finish } } }), Encoding.UTF8, "application/json")
    };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
