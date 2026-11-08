using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qiban.Core;

namespace Qiban.DesktopTests;

internal static partial class Program
{
    private static async Task VerifyActivityTriggersAsync(string root)
    {
        var profile = new InitiativeProfile(5, 15, .28, 12);
        await using (var server = new InitiativeRouteServer())
        {
            var random = new ActivityTrialRandom { Value = .99 };
            using var scene = new TriggerScene(Path.Combine(root, "browser-retry"), server.Endpoint, profile, random);
            EnableActivityOnly(scene);
            scene.Observe = new("chrome", "浏览器");
            await scene.Tick();
            Check(scene.Timer.Interval == TimeSpan.FromSeconds(2)
                && scene.Control<TextBlock>("InitiativeStatus").Text.Contains("前台：浏览器（chrome）"),
                "desktop activity samples every two seconds and displays the currently recognized browser");
            await AdvanceActivityAsync(scene, 2);
            int firstTrialCalls = random.Calls;
            Check(server.Chats.IsEmpty && scene.Scheduler.ActivityRetryInterval == TimeSpan.FromSeconds(36)
                && scene.Control<TextBlock>("InitiativeStatus").Text.Contains("36 秒后重新评估"),
                "a missed browser probability trial displays its 36-second retry rather than the 12-minute message cooldown");
            scene.Window.Width = 940;
            scene.Window.Height = 680;
            Click(scene.Control<Button>("SettingsNavButton"));
            Descendants(scene.Window).OfType<TabItem>().Single(t => Equals(t.Header, "主动聊天")).IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            scene.Window.UpdateLayout();
            Capture(scene.Window, Path.Combine(root, "activity-status.png"));
            random.Value = 0;
            await AdvanceActivityAsync(scene, 34);
            Check(random.Calls == firstTrialCalls && server.Chats.IsEmpty,
                "two-second foreground polling does not repeat probability trials before the retry deadline");
            await AdvanceActivityAsync(scene, 2);
            Check(server.Chats.Count == 1 && scene.Friend.Messages.Single().Trigger == "activity"
                && scene.Friend.Messages.Single().Status == "complete" && server.Chats.Single().Body.Contains("chrome")
                && scene.Friend.Messages.All(m => m.Role != "user"),
                "sustained browser use retries at 36 seconds and obtains a provider-written activity reply without an idle prerequisite");
            Check(scene.Scheduler.GetActivityEligibleAt(scene.Now, scene.Observe!) == scene.Now.AddMinutes(12)
                && scene.Control<TextBlock>("InitiativeStatus").Text.Contains("720 秒后重新评估"),
                "a delivered activity reply retains the full role-specific cooldown and displays its next evaluation");
            int completedCalls = random.Calls;
            scene.Observe = new("vlc", "视频播放器");
            await AdvanceActivityAsync(scene, 40);
            Check(server.Chats.Count == 1 && random.Calls == completedCalls,
                "switching to a video player cannot bypass the successful-message cooldown");
        }

        await using (var server = new InitiativeRouteServer())
        {
            var random = new ActivityTrialRandom { Value = .99 };
            using var scene = new TriggerScene(Path.Combine(root, "left-browser"), server.Endpoint, profile, random);
            EnableActivityOnly(scene);
            scene.Observe = new("msedge", "浏览器");
            await scene.Tick();
            await AdvanceActivityAsync(scene, 2);
            random.Value = 0;
            scene.Observe = null;
            await AdvanceActivityAsync(scene, 40);
            string status = scene.Control<TextBlock>("InitiativeStatus").Text;
            Check(server.Chats.IsEmpty && status.Contains("最近：浏览器（msedge）") && status.Contains("等待前台应用")
                && !status.Contains("前台：浏览器"),
                "returning to the companion keeps a recent observation for display without retrying it as current activity");
            scene.Available = false;
            scene.Observe = new("vlc", "视频播放器");
            await AdvanceActivityAsync(scene, 4);
            Check(server.Chats.IsEmpty && scene.Control<TextBlock>("InitiativeStatus").Text == "会话暂停",
                "unavailable desktop sessions suspend activity recognition and dispatch");
            scene.Available = true;
            scene.Observe = null;
            await AdvanceActivityAsync(scene, 4);
            Check(server.Chats.IsEmpty && !scene.Control<TextBlock>("InitiativeStatus").Text.Contains("vlc"),
                "session resume does not manufacture a video-player event from unavailable-session samples");
        }

        await using (var server = new InitiativeRouteServer())
        using (var scene = new TriggerScene(Path.Combine(root, "player-stability"), server.Endpoint, profile))
        {
            EnableActivityOnly(scene);
            scene.Control<TextBox>("InputBox").Text = "正在输入当前话题";
            scene.Observe = new("chrome", "浏览器");
            await scene.Tick();
            scene.Now = scene.Now.AddSeconds(1);
            scene.Observe = new("vlc", "视频播放器");
            await scene.Tick();
            scene.Now = scene.Now.AddSeconds(1);
            scene.Observe = null;
            await scene.Tick();
            Check(server.Chats.IsEmpty,
                "foreground switches shorter than two seconds do not send a stale browser or player message");
            scene.Observe = new("vlc", "视频播放器");
            await scene.Tick();
            scene.Now = scene.Now.AddSeconds(1);
            await scene.Tick();
            Check(server.Chats.IsEmpty, "a newly recognized player must remain foreground through the stability interval");
            await AdvanceActivityAsync(scene, 1);
            Check(server.Chats.Count == 1 && scene.Friend.Messages.Single().Trigger == "activity"
                && server.Chats.Single().Body.Contains("vlc"),
                "two seconds of stable player use reaches the external model even immediately after composer input");
        }

        await using (var server = new InitiativeRouteServer())
        using (var scene = new TriggerScene(Path.Combine(root, "minimized-timer"), server.Endpoint, profile))
        {
            EnableActivityOnly(scene);
            scene.Observe = new("vlc", "视频播放器");
            scene.Window.WindowState = WindowState.Minimized;
            await scene.Tick();
            scene.Now = scene.Now.AddSeconds(2);
            scene.Timer.Interval = TimeSpan.FromMilliseconds(100);
            scene.Timer.Start();
            await UntilAsync(() => scene.Friend.Messages.Any(m => m.Trigger == "activity" && m.Status == "complete"),
                "minimized window activity DispatcherTimer reply");
            scene.Timer.Stop();
            Check(server.Chats.Count == 1 && scene.Window.WindowState == WindowState.Minimized,
                "the actual DispatcherTimer dispatches a player activity reply while the companion window is minimized");
        }
    }

    private static void EnableActivityOnly(TriggerScene scene)
    {
        scene.State.Settings.Initiative.IdleEnabled = false;
        scene.State.Settings.Initiative.ActivityEnabled = true;
    }

    private static async Task AdvanceActivityAsync(TriggerScene scene, double seconds)
    {
        while (seconds > 0)
        {
            double step = Math.Min(2, seconds);
            scene.Now = scene.Now.AddSeconds(step);
            seconds -= step;
            await scene.Tick();
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    private sealed class ActivityTrialRandom : Random
    {
        public double Value { get; set; }
        public int Calls { get; private set; }
        public override double NextDouble() { Calls++; return Value; }
    }
}
