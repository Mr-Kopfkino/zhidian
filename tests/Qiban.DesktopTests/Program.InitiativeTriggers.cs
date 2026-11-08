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
    private static async Task VerifyInitiativeTriggersAsync(string root)
    {
        var fast = new InitiativeProfile(.5, 1, .8, .5);
        await using (var server = new InitiativeRouteServer())
        using (var scene = new TriggerScene(Path.Combine(root, "cached-deadline"), server.Endpoint, fast))
        {
            await scene.Advance(20);
            Check(server.Chats.IsEmpty, "actual initiative tick waits until the cached role's idle deadline");
            await scene.Advance(10);
            var answer = scene.Friend.Messages.Single();
            Check(server.Chats.Count == 1 && server.Profiles.IsEmpty && answer.Trigger == "idle" && answer.Status == "complete"
                && scene.Store.Load().Friends[0].Messages.Single().Text == answer.Text,
                "actual idle tick dispatches and persists a provider-written reply without directly invoking SendAsync");
        }

        await using (var server = new InitiativeRouteServer { HoldProfile = true })
        using (var scene = new TriggerScene(Path.Combine(root, "pending-analysis"), server.Endpoint))
        {
            await scene.Tick();
            await UntilAsync(() => server.Profiles.Count == 1, "pending background cadence request");
            var deadline = scene.Scheduler.NextIdleAt;
            while (scene.Now.AddSeconds(10) < deadline) await scene.Advance(10);
            scene.Now = deadline;
            scene.Timer.Interval = TimeSpan.FromMilliseconds(100);
            scene.Timer.Start();
            await UntilAsync(() => server.Chats.Count == 1, "dispatcher timer reaches chat while analysis is held");
            scene.Timer.Stop();
            await UntilAsync(() => scene.Friend.Messages.Any(m => m.Trigger == "idle" && m.Status == "complete"), "dispatcher timer during held profile request");
            await UntilAsync(() => scene.Private("_profileCts") == null, "cancelled auxiliary analysis after proactive dispatch");
            Check(server.Chats.Count == 1 && server.Profiles.Count == 1 && scene.Friend.InitiativeProfile == null,
                "a real DispatcherTimer tick sends proactive chat despite pending cadence analysis and cancels the auxiliary request");
            Check(scene.Scheduler.UnansweredCount == 1,
                "timer-generated proactive output counts once after cancelling auxiliary analysis");
        }

        await using (var server = new InitiativeRouteServer { FailProfile = true })
        using (var scene = new TriggerScene(Path.Combine(root, "failed-analysis"), server.Endpoint))
        {
            await scene.Tick();
            await UntilAsync(() => server.Profiles.Count == 1 && scene.Private("_profileCts") == null, "failed background cadence request");
            for (int i = 0; i < 3; i++) await scene.Advance(10);
            Check(server.Profiles.Count == 1 && scene.Friend.InitiativeProfile == null,
                "failed cadence analysis backs off rather than issuing a request on every timer tick");
            Click(scene.Control<Button>("SettingsNavButton"));
            Descendants(scene.Window).OfType<TabItem>().Single(t => Equals(t.Header, "主动聊天")).IsSelected = true;
            scene.Window.UpdateLayout();
            Capture(scene.Window, Path.Combine(root, "initiative-status.png"));
            var deadline = scene.Scheduler.NextIdleAt;
            while (scene.Now < deadline) await scene.Advance(Math.Min(10, (deadline - scene.Now).TotalSeconds));
            Check(server.Chats.Count == 1 && scene.Friend.Messages.Single().Trigger == "idle"
                && scene.Friend.Messages.Single().Status == "complete" && scene.Friend.InitiativeProfile == null,
                "cadence failure cannot permanently block the actual idle-to-model generation path");
        }

        await using (var server = new InitiativeRouteServer { HoldProfile = true })
        using (var scene = new TriggerScene(Path.Combine(root, "elapsed-analysis"), server.Endpoint))
        {
            await scene.Tick();
            await UntilAsync(() => server.Profiles.Count == 1, "profile analysis before elapsed idle time");
            await scene.Advance(40);
            server.ReleaseProfile.TrySetResult();
            await UntilAsync(() => scene.Friend.InitiativeProfile != null && scene.Private("_profileCts") == null, "completed cadence analysis");
            Check(scene.Scheduler.NextIdleAt <= scene.Now,
                "installing a model-derived cadence keeps elapsed idle time instead of starting a fresh countdown");
            await scene.Tick();
            Check(server.Chats.Count == 1 && scene.Friend.Messages.Single().Trigger == "idle"
                && scene.Store.Load().Friends[0].InitiativeProfile != null,
                "a deadline already reached during analysis triggers immediately after the profile becomes available");
        }

        await using (var server = new InitiativeRouteServer { HoldChatIndex = 1 })
        using (var scene = new TriggerScene(Path.Combine(root, "busy-activity"), server.Endpoint, new(60, 90, .8, .5)))
        {
            scene.State.Settings.Initiative.IdleEnabled = false;
            scene.State.Settings.Initiative.ActivityEnabled = true;
            scene.Observe = new("fixture-browser", "浏览器");
            scene.Control<TextBox>("InputBox").Text = "正在讨论当前的话题。";
            var ordinary = scene.Send();
            await UntilAsync(() => server.Chats.Count == 1, "ordinary reply held before proactive observation");
            await scene.Tick();
            await scene.Advance(2);
            Check(scene.Scheduler.IsPending && server.Chats.Count == 1 && scene.Friend.Messages.Count == 2,
                "stable foreground activity reserves one interruption during an ordinary reply without waiting for an idle deadline");
            server.ReleaseChat.TrySetResult();
            await ordinary;
            await scene.Tick();
            await UntilAsync(() => scene.Friend.Messages.Any(m => m.Trigger == "activity" && m.Status == "complete"), "queued activity provider reply");
            Check(server.Chats.Count == 2 && scene.Friend.Messages.Count(m => m.Role == "user") == 1
                && scene.Friend.Messages[^1].Trigger == "activity" && scene.Friend.Messages[^1].Text == "主动调度测试回复。"
                && server.Chats.Last().Body.Contains("fixture-browser"),
                "the queued foreground trigger reaches the model after the current reply, without fake user input or duplicate output");
        }

        await using (var server = new InitiativeRouteServer())
        using (var scene = new TriggerScene(Path.Combine(root, "session-pause"), server.Endpoint, fast))
        {
            await scene.Advance(20);
            scene.Available = false;
            await scene.Advance(30);
            Check(server.Chats.IsEmpty && scene.Friend.Messages.Count == 0,
                "unavailable session prevents real idle dispatch even when its deadline passes");
            scene.Available = true;
            await scene.Advance(10);
            Check(server.Chats.IsEmpty && scene.Scheduler.NextIdleAt > scene.Now,
                "session resume restarts a bounded idle wait instead of sending accumulated messages");
            await scene.Advance(30);
            Check(server.Chats.Count == 1 && scene.Friend.Messages.Single().Status == "complete",
                "idle scheduling resumes after the session becomes available again");
        }

        await using (var server = new InitiativeRouteServer())
        using (var scene = new TriggerScene(Path.Combine(root, "role-switch"), server.Endpoint, fast))
        {
            var first = scene.Friend;
            var second = new FriendProfile { Id = "timer-second", Name = "第二位朋友", InitiativeProfile = fast };
            scene.State.Friends.Add(second);
            scene.Cache(second);
            scene.Call("RefreshFriends");
            await scene.Advance(20);
            scene.Control<ListBox>("FriendList").SelectedItem = second;
            await scene.Advance(10);
            Check(server.Chats.IsEmpty && first.Messages.Count == 0 && second.Messages.Count == 0,
                "switching roles discards the former role's idle deadline");
            await scene.Advance(20);
            Check(server.Chats.Count == 1 && first.Messages.Count == 0 && second.Messages.Single().Trigger == "idle",
                "the selected role alone receives its timer-triggered proactive reply");
        }

        await using (var server = new InitiativeRouteServer { HoldProfile = true })
        using (var scene = new TriggerScene(Path.Combine(root, "stale-analysis"), server.Endpoint))
        {
            var first = scene.Friend;
            var second = new FriendProfile { Id = "analysis-second", Name = "另一位朋友", InitiativeProfile = fast };
            scene.State.Friends.Add(second); scene.Cache(second); scene.Call("RefreshFriends");
            await scene.Tick();
            await UntilAsync(() => server.Profiles.Count == 1, "analysis before role selection changes");
            scene.Control<ListBox>("FriendList").SelectedItem = second;
            server.ReleaseProfile.TrySetResult();
            await UntilAsync(() => scene.Private("_profileCts") == null, "old role analysis cancellation");
            await scene.Advance(30);
            Check(first.InitiativeProfile == null && first.Messages.Count == 0 && second.InitiativeProfile == fast
                && second.Messages.Single().Trigger == "idle" && server.Profiles.Count == 1,
                "a cancelled prior-role analysis cannot overwrite the selected role's cadence or prevent its timer reply");
        }
    }

    private sealed class ZeroInitiativeRandom : Random
    {
        public override double NextDouble() => 0;
    }

    private sealed class TriggerScene : IDisposable
    {
        public PortableStore Store { get; }
        public AppState State { get; } = new();
        public MainWindow Window { get; }
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public bool Available { get; set; } = true;
        public ActivityObservation? Observe { get; set; }
        public FriendProfile Friend => State.Friends.Single(f => f.Id == State.SelectedFriendId);
        public DispatcherTimer Timer => (DispatcherTimer)Private("_initiativeTimer")!;
        public InitiativeScheduler Scheduler => (InitiativeScheduler)Private("_initiativeScheduler")!;
        public TriggerScene(string root, string endpoint, InitiativeProfile? profile = null, Random? random = null)
        {
            Store = new PortableStore(root); Store.Initialize();
            State.Settings.Endpoint = endpoint; State.Settings.Model = "initiative-trigger-fixture";
            State.Settings.Initiative.ActivityEnabled = false;
            if (profile != null) { Friend.InitiativeProfile = profile; Cache(Friend); }
            Store.Save(State);
            var environment = new InitiativeEnvironment
            {
                Now = () => Now, IsSessionAvailable = () => Available, ObserveActivity = () => Observe,
                Random = random ?? new ZeroInitiativeRandom()
            };
            Window = new MainWindow(Store, State, enableInitiative: true, initiativeEnvironment: environment, enableMemoryLearning: false)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            Window.Show(); Timer.Stop();
        }
        public void Cache(FriendProfile friend) => friend.InitiativeProfileKey = (string)typeof(MainWindow)
            .GetMethod("InitiativeKey", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [State.Settings, friend])!;
        public T Control<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
        public object? Private(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);
        public object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, args);
        public Task Tick() => (Task)Call("TickInitiativeAsync")!;
        public Task Send() => (Task)Call("SendAsync", false, null)!;
        public async Task Advance(double seconds)
        {
            while (seconds > 0)
            {
                double step = Math.Min(10, seconds); Now = Now.AddSeconds(step); seconds -= step;
                await Tick();
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        public void Dispose() { Timer.Stop(); Window.Close(); }
    }

    /// <summary>Routes background profile analysis and chat independently so a hung analyzer cannot serialize the fixture.</summary>
    private sealed class InitiativeRouteServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(60));
        private readonly List<Task> serving = [];
        private readonly Task accepting;
        public ConcurrentQueue<RecordedRequest> Profiles { get; } = new();
        public ConcurrentQueue<RecordedRequest> Chats { get; } = new();
        public TaskCompletionSource ReleaseProfile { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseChat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldProfile { get; init; }
        public bool FailProfile { get; init; }
        public int HoldChatIndex { get; init; } = -1;
        public string Endpoint { get; }
        public InitiativeRouteServer()
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            accepting = Task.Run(async () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                        serving.Add(Serve(client));
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            });
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            try
            {
                await using var stream = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, cancellation.Token) == 0) throw new IOException("Initiative fixture header ended early.");
                    header.Add(one[0]); int count = header.Count;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                int size = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim(), CultureInfo.InvariantCulture);
                if (size is < 0 or > 1_000_000) throw new InvalidDataException("Initiative fixture request too large.");
                byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cancellation.Token);
                var record = new RecordedRequest(lines[0].Split(' ')[1], Encoding.UTF8.GetString(bytes));
                using var payload = JsonDocument.Parse(bytes);
                bool profile = payload.RootElement.GetProperty("messages").EnumerateArray()
                    .Any(m => m.GetProperty("content").GetString()?.Contains("IdleMinMinutes", StringComparison.Ordinal) == true);
                bool fail = profile && FailProfile;
                if (profile)
                {
                    Profiles.Enqueue(record);
                    if (HoldProfile) await ReleaseProfile.Task.WaitAsync(cancellation.Token);
                }
                else
                {
                    Chats.Enqueue(record);
                    if (Chats.Count == HoldChatIndex) await ReleaseChat.Task.WaitAsync(cancellation.Token);
                }
                string reply = profile ? "{\"IdleMinMinutes\":0.5,\"IdleMaxMinutes\":1,\"ActivityProbability\":0.8,\"ActivityCooldownMinutes\":0.5}" : "主动调度测试回复。";
                string body = fail ? "{\"error\":\"fixture cadence unavailable\"}"
                    : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = reply } } } });
                byte[] content = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(fail ? "503 Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), cancellation.Token);
                await stream.WriteAsync(content, cancellation.Token); await stream.FlushAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (IOException) { /* Client cancellation closes held model requests. */ }
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
