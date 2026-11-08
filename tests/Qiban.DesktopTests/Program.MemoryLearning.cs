using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qiban.Core;
using Qiban.Desktop;

namespace Qiban.DesktopTests;

internal static partial class Program
{
    private static async Task VerifyMemoryLearningAsync(string root)
    {
        await using (var server = new MemoryLearningServer())
        {
            string path = Path.Combine(root, "automatic");
            string learnedId;
            using (var scene = new MemoryLearningScene(path, server.Endpoint, timer: true))
            {
                scene.Control<TextBox>("InputBox").Text = "我喜欢乌龙茶，周日想去海边散步。";
                await scene.Send();
                Check(server.Chats.Count == 1 && server.Learning.IsEmpty && scene.Friend.Memories.Count == 0,
                    "ordinary conversation completes before the deferred automatic-memory request starts");
                Click(scene.Control<Button>("MemoryNavButton"));
                scene.Control<TextBox>("MemoryContent").Text = "尚未提交的记忆编辑";
                scene.Control<TextBox>("MemoryExpiry").Text = "2030-01-02";
                scene.Control<CheckBox>("MemoryPinned").IsChecked = true;
                scene.Control<TextBox>("InputBox").Text = "尚未发送的聊天草稿";
                await UntilAsync(() => scene.Friend.Memories.Count == 1 && scene.Private("_memoryCts") == null,
                    "real DispatcherTimer automatically learns from ordinary conversation");
                scene.Timer.Stop();
                var memory = scene.Friend.Memories.Single();
                learnedId = memory.Id;
                var saved = scene.Store.Load().Friends[0];
                Check(server.Learning.Count == 1 && memory.Origin == "model" && memory.State == "approved"
                    && memory.SourceMessageId == scene.Friend.Messages[0].Id && saved.Memories.Single().Id == learnedId,
                    "model-selected natural-language facts become sourced approved memories without a remember command or manual confirmation");
                Check(scene.Friend.Learning.Summary.Count == 2 && saved.Learning.Summary.Count == 2
                    && scene.Control<TextBox>("ConversationSummary").Text.Contains("用户：")
                    && scene.Control<TextBox>("ConversationSummary").Text.Contains("角色：")
                    && scene.Control<TextBlock>("MemoryLearningStatus").Text.Contains("已整理"),
                    "automatic conversation summaries distinguish user statements and character suggestions and persist with visible status");
                Check(scene.Control<TextBox>("MemoryContent").Text == "尚未提交的记忆编辑"
                    && scene.Control<TextBox>("MemoryExpiry").Text == "2030-01-02"
                    && scene.Control<CheckBox>("MemoryPinned").IsChecked == true
                    && scene.Control<TextBox>("InputBox").Text == "尚未发送的聊天草稿",
                    "background memory refresh preserves unsaved memory fields and the chat draft");
                await scene.Learn();
                Check(server.Learning.Count == 1 && scene.Friend.Memories.Count == 1,
                    "completed learning cursors prevent repeated model calls and duplicate memories without new user messages");
                Capture(scene.Window, Path.Combine(path, "automatic-memory.png"));
            }
            using (var scene = new MemoryLearningScene(path, server.Endpoint))
            {
                await scene.Learn();
                Check(scene.Friend.Memories.Single().Id == learnedId && scene.Friend.Learning.Summary.Count == 2
                    && server.Learning.Count == 1 && scene.Control<TextBox>("ConversationSummary").Text.Contains("乌龙茶"),
                    "reopening restores automatically learned facts, summary and processed cursor without analyzing the same conversation again");
                Click(scene.Control<Button>("SettingsNavButton"));
                scene.Control<CheckBox>("AutoMemoryEnabled").IsChecked = false;
                scene.Control<CheckBox>("AutoSummaryEnabled").IsChecked = false;
                Click(scene.Control<Button>("SaveSettingsButton"));
                scene.Control<TextBox>("InputBox").Text = "我喜欢清晨散步。";
                await scene.Send();
                await scene.Learn();
                var settings = scene.Store.Load().Settings;
                Check(!settings.AutoMemoryEnabled && !settings.AutoSummaryEnabled && server.Learning.Count == 1
                    && scene.Control<TextBlock>("MemoryLearningStatus").Text.Contains("已关闭"),
                    "both automation switches persist and prevent analysis of new conversation when disabled");

                scene.Control<CheckBox>("AutoSummaryEnabled").IsChecked = true;
                Click(scene.Control<Button>("SaveSettingsButton"));
                await scene.Learn();
                Check(scene.Friend.Memories.Count == 1 && scene.Friend.Learning.Summary.Any(s => s.Text.Contains("清晨散步"))
                    && server.Learning.Count == 2,
                    "summary-only mode updates the conversation summary without adding long-term facts");
                string summary = JsonSerializer.Serialize(scene.Friend.Learning.Summary);
                scene.Control<CheckBox>("AutoMemoryEnabled").IsChecked = true;
                scene.Control<CheckBox>("AutoSummaryEnabled").IsChecked = false;
                Click(scene.Control<Button>("SaveSettingsButton"));
                scene.Control<TextBox>("InputBox").Text = "我喜欢冬天的雪景。";
                await scene.Send();
                await scene.Learn();
                Check(scene.Friend.Memories.Count == 2 && JsonSerializer.Serialize(scene.Friend.Learning.Summary) == summary,
                    "memory-only mode learns new facts while preserving the stored summary without regenerating it");
                scene.Control<CheckBox>("AutoSummaryEnabled").IsChecked = true;
                Click(scene.Control<Button>("SaveSettingsButton"));
                Click(scene.Control<Button>("MemoryNavButton"));
                Click(scene.Control<Button>("ClearSummaryButton"));
                int previousCalls = server.Learning.Count;
                await scene.Learn();
                Check(scene.Friend.Learning.Summary.Count == 0 && scene.Store.Load().Friends[0].Learning.Summary.Count == 0
                    && scene.Friend.Learning.SummarySuppressedThroughMessageId == scene.Friend.Messages[^1].Id
                    && scene.Control<TextBox>("ConversationSummary").Text == "暂无摘要" && server.Learning.Count == previousCalls,
                    "clearing the summary persists a suppression boundary and cannot regenerate the same already processed conversation");
            }
        }

        await using (var server = new MemoryLearningServer { FailLearning = true })
        using (var scene = new MemoryLearningScene(Path.Combine(root, "failure-retry"), server.Endpoint, seed: true))
        {
            Click(scene.Control<Button>("AnalyzeMemoryButton"));
            await UntilAsync(() => server.Learning.Count == 1 && scene.Private("_memoryCts") == null, "failed memory request");
            Check(scene.Friend.Memories.Count == 0 && scene.Friend.Learning.LastProcessedMessageId == ""
                && scene.Store.Load().Friends[0].Learning.LastProcessedMessageId == ""
                && scene.Control<TextBlock>("MemoryLearningStatus").Text.Contains("2 分钟")
                && (DateTimeOffset)scene.Private("_memoryNextAt")! > DateTimeOffset.UtcNow.AddSeconds(100),
                "analysis failure keeps the durable cursor unchanged and exposes a bounded retry delay");
            for (int i = 0; i < 3; i++) await scene.Learn(immediate: false);
            Check(server.Learning.Count == 1, "automatic ticks respect analysis backoff instead of flooding a failing provider");
            server.FailLearning = false;
            Click(scene.Control<Button>("AnalyzeMemoryButton"));
            await UntilAsync(() => scene.Friend.Memories.Count == 1 && scene.Private("_memoryCts") == null, "manual memory retry");
            Check(server.Learning.Count == 2 && scene.Friend.Learning.LastProcessedMessageId == scene.Friend.Messages[^1].Id,
                "the immediate-analysis button retries pending conversation successfully without waiting for automatic backoff");
        }

        await using (var server = new MemoryLearningServer { InvalidEvidence = true })
        using (var scene = new MemoryLearningScene(Path.Combine(root, "invalid-evidence"), server.Endpoint, seed: true))
        {
            await scene.Learn();
            Check(scene.Friend.Memories.Count == 0 && scene.Friend.Learning.Summary.Count == 0
                && scene.Friend.Learning.LastProcessedMessageId == "" && scene.Store.Load().Friends[0].Memories.Count == 0,
                "model output without real source evidence cannot partially write facts, summary or processing progress");
        }

        foreach (bool conflict in new[] { false, true })
        {
            await using var server = new MemoryLearningServer { HoldLearningIndex = 1 };
            using var scene = new MemoryLearningScene(Path.Combine(root, conflict ? "save-revision-conflict" : "save-write-failure"), server.Endpoint, seed: true);
            Click(scene.Control<Button>("MemoryNavButton"));
            scene.Control<TextBox>("MemoryContent").Text = "保存故障期间的未提交草稿";
            var original = scene.Friend;
            var pending = scene.Learn();
            await UntilAsync(() => server.Learning.Count == 1, "held analysis before persistence failure");
            FileStream? lockedBackup = null;
            try
            {
                if (conflict)
                {
                    var concurrent = scene.Store.Load();
                    concurrent.Friends[0].Memories.Add(new() { Content = "另一写入者已提交的记忆", State = "approved" });
                    scene.Store.Save(concurrent);
                }
                else lockedBackup = new FileStream(Path.Combine(scene.Store.DataDirectory, "state.backup.json"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
                server.ReleaseLearning.TrySetResult();
                await pending;
                // Save queues a rollback render on the dispatcher. Assert after it has run,
                // rather than observing the retry deadline before RenderFriend can reset it.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                scene.Window.UpdateLayout();
            }
            finally { lockedBackup?.Dispose(); }
            var retryAt = (DateTimeOffset)scene.Private("_memoryNextAt")!;
            for (int i = 0; i < 3; i++) await scene.Learn(immediate: false);
            var durable = scene.Store.Load().Friends[0];
            Check(!ReferenceEquals(scene.Friend, original) && scene.Friend.Memories.Count == (conflict ? 1 : 0)
                && scene.Friend.Memories.All(m => m.Origin != "model")
                && durable.Memories.Count == scene.Friend.Memories.Count && durable.Learning.Summary.Count == 0
                && scene.Friend.Learning.LastProcessedMessageId == "" && durable.Learning.LastProcessedMessageId == ""
                && retryAt > DateTimeOffset.UtcNow.AddSeconds(100)
                && scene.Control<TextBlock>("MemoryLearningStatus").Text.Contains("2 分钟")
                && scene.Control<TextBox>("MemoryContent").Text == "保存故障期间的未提交草稿"
                && server.Learning.Count == 1,
                $"analysis {(conflict ? "revision conflict" : "write failure")} rolls back uncommitted results and preserves two-minute backoff, status and draft after queued window refresh");
        }

        await using (var server = new MemoryLearningServer { HoldLearningIndex = 1 })
        using (var scene = new MemoryLearningScene(Path.Combine(root, "new-message-cancels"), server.Endpoint, seed: true))
        {
            string previousUser = scene.Friend.Messages[0].Id;
            var pending = scene.Learn();
            await UntilAsync(() => server.Learning.Count == 1, "held analysis before new message");
            scene.Control<TextBox>("InputBox").Text = "我现在想讨论音乐。";
            await scene.Send();
            server.ReleaseLearning.TrySetResult();
            await pending;
            Check(server.Chats.Count == 1 && scene.Friend.Memories.Count == 0 && scene.Friend.Learning.Summary.Count == 0,
                "new conversation cancels auxiliary analysis and completes chat without committing the stale result");
            await scene.Learn();
            Check(scene.Friend.Memories.Single().SourceMessageId != previousUser
                && scene.Friend.Memories.Single().SourceMessageId == scene.Friend.Messages[^2].Id,
                "learning resumes from the latest completed conversation after cancellation");
        }

        await using (var server = new MemoryLearningServer { HoldLearningIndex = 1 })
        using (var scene = new MemoryLearningScene(Path.Combine(root, "role-switch-cancels"), server.Endpoint, seed: true))
        {
            var previous = scene.Friend;
            var second = new FriendProfile { Id = "learning-second", Name = "另一位角色" };
            second.Messages.Add(new() { Text = "我喜欢下雨的天气。" });
            second.Messages.Add(new() { Role = "assistant", Text = "我们可以聊聊雨天。" });
            scene.State.Friends.Add(second);
            scene.Call("RefreshFriends");
            var pending = scene.Learn();
            await UntilAsync(() => server.Learning.Count == 1, "held analysis before role selection");
            scene.Control<ListBox>("FriendList").SelectedItem = second;
            server.ReleaseLearning.TrySetResult();
            await pending;
            Check(previous.Memories.Count == 0 && previous.Learning.Summary.Count == 0 && second.Memories.Count == 0,
                "switching characters cancels prior-role analysis without contaminating either profile");
            await scene.Learn();
            Check(second.Memories.Single().SourceMessageId == second.Messages[0].Id && previous.Memories.Count == 0
                && scene.Store.Load().Friends.Single(f => f.Id == second.Id).Memories.Count == 1,
                "the newly selected character alone receives its own learned and persisted memories");
        }

        await using (var server = new MemoryLearningServer { HoldLearningIndex = 1 })
        using (var scene = new MemoryLearningScene(Path.Combine(root, "archive-cancels"), server.Endpoint, seed: true))
        {
            var old = new MemoryItem { Content = "旧的已确认条目", State = "approved", SourceMessageId = scene.Friend.Messages[0].Id };
            scene.Friend.Memories.Add(old);
            scene.Store.Save(scene.State);
            Click(scene.Control<Button>("MemoryNavButton"));
            scene.Control<ListBox>("MemoryList").SelectedIndex = 0;
            var pending = scene.Learn();
            await UntilAsync(() => server.Learning.Count == 1, "held analysis before memory archival");
            Click(FindButton(scene.Window, "归档"));
            server.ReleaseLearning.TrySetResult();
            await pending;
            await scene.Learn();
            Check(old.State == "archived" && scene.Friend.Memories.Count == 1 && scene.Friend.Learning.Summary.Count == 0
                && scene.Friend.Learning.SuppressedSourceIds.Contains(old.SourceMessageId) && server.Learning.Count == 1,
                "archiving a source during analysis cancels the result and prevents archived information from being learned again");
        }

        await using (var server = new MemoryLearningServer { HoldLearningIndex = 1 })
        using (var scene = new MemoryLearningScene(Path.Combine(root, "close-cancels"), server.Endpoint, seed: true))
        {
            var pending = scene.Learn();
            await UntilAsync(() => server.Learning.Count == 1, "held analysis before closing");
            scene.Window.Close();
            server.ReleaseLearning.TrySetResult();
            await pending;
            var saved = scene.Store.Load().Friends[0];
            Check(saved.Memories.Count == 0 && saved.Learning.Summary.Count == 0 && saved.Learning.LastProcessedMessageId == ""
                && !scene.Timer.IsEnabled && scene.Private("_memoryCts") == null,
                "closing cancels analysis and its timer without writing a late result into the saved profile");
        }
    }

    private sealed class MemoryLearningScene : IDisposable
    {
        public PortableStore Store { get; }
        public AppState State { get; }
        public MainWindow Window { get; }
        public FriendProfile Friend => State.Friends.Single(f => f.Id == State.SelectedFriendId);
        public DispatcherTimer Timer => (DispatcherTimer)Private("_memoryTimer")!;
        public MemoryLearningScene(string root, string endpoint, bool seed = false, bool timer = false)
        {
            Store = new PortableStore(root); Store.Initialize(); State = Store.Load();
            State.Settings.Endpoint = endpoint; State.Settings.Model = "memory-fixture";
            if (seed)
            {
                Friend.Messages.Add(new() { Text = "我喜欢乌龙茶。" });
                Friend.Messages.Add(new() { Role = "assistant", Text = "我们可以聊聊饮茶。" });
            }
            Store.Save(State);
            Window = new MainWindow(Store, State, enableInitiative: false, enableMemoryLearning: true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowInTaskbar = false, ShowActivated = false
            };
            if (!timer) Timer.Stop();
            Window.Show();
        }
        public T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
        public object? Private(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Window);
        public object? Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Window, arguments);
        public Task Send() => (Task)Call("SendAsync", false, null)!;
        public Task Learn(bool immediate = true) => (Task)Call("RunMemoryLearningAsync", immediate)!;
        public void Dispose() { Timer.Stop(); Window.Close(); }
    }

    private sealed class MemoryLearningServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
        private readonly List<Task> serving = [];
        private readonly Task accepting;
        public ConcurrentQueue<RecordedRequest> Learning { get; } = new();
        public ConcurrentQueue<RecordedRequest> Chats { get; } = new();
        public TaskCompletionSource ReleaseLearning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailLearning { get; set; }
        public bool InvalidEvidence { get; init; }
        public int HoldLearningIndex { get; init; } = -1;
        public string Endpoint { get; }
        public MemoryLearningServer()
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            accepting = Task.Run(async () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                        serving.Add(Serve(await listener.AcceptTcpClientAsync(cancellation.Token)));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            });
        }
        private static JsonElement Property(JsonElement element, string name) => element.EnumerateObject()
            .Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        private async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                await using var stream = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, cancellation.Token) == 0) throw new IOException("Memory fixture header ended early.");
                    header.Add(one[0]); int count = header.Count;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                int size = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim(), CultureInfo.InvariantCulture);
                if (size is < 0 or > 1_000_000) throw new InvalidDataException("Memory fixture request too large.");
                byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation.Token);
                var record = new RecordedRequest(lines[0].Split(' ')[1], Encoding.UTF8.GetString(bytes));
                using var payload = JsonDocument.Parse(bytes);
                var messages = payload.RootElement.GetProperty("messages");
                bool learning = messages[0].GetProperty("content").GetString()!.Contains("对话记忆整理器", StringComparison.Ordinal);
                bool fail = learning && FailLearning;
                string reply;
                if (learning)
                {
                    Learning.Enqueue(record);
                    if (Learning.Count == HoldLearningIndex) await ReleaseLearning.Task.WaitAsync(cancellation.Token);
                    using var input = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
                    var records = input.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                    var user = records.Last(m => Property(m, "Role").GetString() == "user");
                    string id = Property(user, "Id").GetString()!, text = Property(user, "Text").GetString()!;
                    var result = new MemoryLearningResult();
                    if (input.RootElement.GetProperty("enableMemories").GetBoolean())
                        result.Memories.Add(new() { Content = "用户表示：" + text, Category = "偏好", Confidence = .95,
                            SourceMessageId = id, Evidence = InvalidEvidence ? "原文并没有这段内容" : text });
                    if (input.RootElement.GetProperty("enableSummary").GetBoolean())
                    {
                        result.Summary.Add(new() { Text = "用户讨论：" + text, SourceMessageId = id, Evidence = text });
                        var assistant = records.LastOrDefault(m => Property(m, "Role").GetString() == "assistant");
                        if (assistant.ValueKind != JsonValueKind.Undefined)
                            result.Summary.Add(new() { Text = "角色建议继续讨论这个话题。", SourceMessageId = Property(assistant, "Id").GetString()!, Evidence = Property(assistant, "Text").GetString()! });
                    }
                    reply = JsonSerializer.Serialize(result);
                }
                else { Chats.Enqueue(record); reply = "好，我们按这个计划继续聊。"; }
                string body = fail ? "{\"error\":\"fixture memory unavailable\"}"
                    : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = reply }, finish_reason = "stop" } } });
                byte[] content = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(fail ? "503 Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), cancellation.Token);
                await stream.WriteAsync(content, cancellation.Token); await stream.FlushAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (IOException) { /* Client cancellation closes held requests. */ }
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel(); listener.Stop();
            try { await accepting; } catch (OperationCanceledException) { }
            await Task.WhenAll(serving);
            cancellation.Dispose();
        }
    }
}
