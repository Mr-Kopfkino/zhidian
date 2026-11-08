using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Qiban.Core;
using Qiban.Desktop;

namespace Qiban.DesktopTests;

internal static partial class Program
{
    private static async Task VerifyEmbeddedVoiceSettingsAsync(string root)
    {
        var store = new PortableStore(root); store.Initialize();
        var state = new AppState();
        state.Friends[0].Messages.Add(new() { Role = "user", Text = "语音设置不应改变这条消息" });
        state.Friends[0].Memories.Add(new() { Content = "保存原有长期记忆", State = "approved" });
        var second = new FriendProfile { Id = "second-engine", Name = "另一角色", Voice = new() { Provider = "OpenAI-compatible",
            Endpoint = "https://speech.example/v1/audio/speech", Model = "cloud-model", Voice = "cloud-voice" } };
        state.Friends.Add(second); store.Save(state);
        var window = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            Width = 940, Height = 680, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            Descendants(window).OfType<TabItem>().Single(item => Equals(item.Header, "角色声音")).IsSelected = true;
            window.UpdateLayout();
            Check((Control<ComboBox>("VoiceProvider").SelectedItem as ComboBoxItem)?.Content?.ToString() == "GPT-SoVITS 内置"
                && !Control<TextBox>("VoiceEndpoint").IsVisible && !Control<PasswordBox>("VoiceKey").IsVisible
                && !Control<TextBox>("VoiceModel").IsVisible && !Control<TextBox>("VoiceName").IsVisible
                && !Control<TextBox>("VoiceTemplate").IsVisible && Control<Border>("VoiceGptPanel").IsVisible,
                "embedded voice settings expose role weights and references without requiring an endpoint key or remote model fields");
            Check(Control<TextBlock>("EmbeddedVoiceStatus").Text == "内置引擎文件缺失",
                "a missing bundled engine is reported in settings rather than pretending an external API is running");
            string originalRecords = JsonSerializer.Serialize(new { state.Friends[0].Messages, state.Friends[0].Memories });
            string secondVoice = JsonSerializer.Serialize(second.Voice);
            Control<TextBox>("VoiceGptWeights").Text = "D:/voices/first.ckpt";
            Control<TextBox>("VoiceSovitsWeights").Text = "D:/voices/first.pth";
            Control<TextBox>("VoiceReference").Text = "D:/voices/first.wav";
            Control<TextBox>("VoiceReferenceText").Text = "これは参考音声です。";
            Control<TextBox>("VoiceLanguage").Text = "ja";
            Click(FindButton(window, "保存设置"));
            var loaded = store.Load();
            Check(loaded.Friends[0].Voice.Provider == "GPT-SoVITS 内置"
                && loaded.Friends[0].Voice.GptWeights == "D:/voices/first.ckpt"
                && loaded.Friends[0].Voice.SovitsWeights == "D:/voices/first.pth"
                && loaded.Friends[0].Voice.ReferenceAudio == "D:/voices/first.wav"
                && loaded.Friends[0].Voice.Language == "ja"
                && JsonSerializer.Serialize(loaded.Friends[1].Voice) == secondVoice
                && JsonSerializer.Serialize(new { loaded.Friends[0].Messages, loaded.Friends[0].Memories }) == originalRecords,
                "embedded per-role model settings persist without changing another voice conversation or approved memory");
            Capture(window, Path.Combine(root, "embedded-voice-settings.png"));
            Control<ListBox>("FriendList").SelectedItem = second;
            Check(Control<TextBox>("VoiceEndpoint").Text == second.Voice.Endpoint && Control<TextBox>("VoiceEndpoint").IsVisible
                && Control<TextBox>("VoiceModel").IsVisible && Control<TextBox>("VoiceName").IsVisible
                && !Control<Border>("VoiceGptPanel").IsVisible && !Control<TextBlock>("EmbeddedVoiceStatus").IsVisible,
                "switching to a compatible cloud role restores its endpoint and voice controls without embedded model fields");
            var providers = Control<ComboBox>("VoiceProvider");
            providers.SelectedItem = providers.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Content, "Custom JSON"));
            Check(Control<TextBox>("VoiceTemplate").IsVisible && !Control<TextBox>("VoiceModel").IsVisible
                && !Control<Border>("VoiceGptPanel").IsVisible,
                "custom speech templates remain accessible only for the custom provider");
            providers.SelectedItem = providers.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Content, "GPT-SoVITS 外接"));
            Control<TextBox>("VoiceEndpoint").Text = "http://127.0.0.1:9880/tts";
            Click(FindButton(window, "保存设置"));
            Check(store.Load().Friends[1].Voice.Provider == "GPT-SoVITS 外接" && Control<TextBox>("VoiceEndpoint").IsVisible
                && Control<Border>("VoiceGptPanel").IsVisible,
                "explicit external GPT-SoVITS remains selectable and persists even on the old default port");
            Control<ListBox>("FriendList").SelectedItem = state.Friends[0];
            Check(Control<TextBox>("VoiceGptWeights").Text == "D:/voices/first.ckpt" && !Control<TextBox>("VoiceEndpoint").IsVisible,
                "returning to an embedded role restores its own weights and hides service connection fields again");

            string cacheFile = Path.Combine(store.CacheDirectory, "embedded-cleanup-sentinel.tmp");
            File.WriteAllText(cacheFile, "temporary");
            typeof(MainWindow).GetMethod("OnCleanCache", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            Check(!File.Exists(cacheFile) && store.Load().Friends[0].Voice.GptWeights == "D:/voices/first.ckpt",
                "manual cache cleanup retains role model references and persisted user state");
        }
        finally { window.Close(); }
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
    }

    private static async Task VerifyDeferredVoiceCacheCleanupAsync(string root)
    {
        await using var firstServer = new LocalVoiceServer();
        await using var nextServer = new LocalVoiceServer();
        var store = new PortableStore(root); store.Initialize();
        var state = new AppState();
        var friend = state.Friends[0];
        friend.Voice = new() { Enabled = true, Provider = "OpenAI-compatible", Endpoint = firstServer.BaseAddress + "/v1/audio/speech" };
        store.Save(state);
        var window = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            Width = 940, Height = 680, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var firstSpeech = Speak("清理之前的语音");
            await firstServer.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
            object? previous = Private("_voiceCts");
            string sentinel = Path.Combine(store.CacheDirectory, "cleanup-sentinel.tmp");
            string session = Path.Combine(store.CacheDirectory, "voice-engine", Guid.NewGuid().ToString("N"));
            File.WriteAllText(sentinel, "temporary");
            Directory.CreateDirectory(Path.Combine(session, "temp", "empty"));
            typeof(MainWindow).GetMethod("OnCleanCache", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            friend.Voice.Endpoint = nextServer.BaseAddress + "/v1/audio/speech";
            Task blocked = Speak("清理期间不应开始这段语音");
            Check(blocked.IsCompletedSuccessfully && Equals(Private("_cleanCacheAfterVoice"), true)
                && ReferenceEquals(Private("_voiceCts"), previous) && !nextServer.Request.IsCompleted,
                "pending manual cache cleanup blocks replacement speech until the cancelled operation finishes");
            await firstSpeech.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Private("_voiceCts") == null && Equals(Private("_cleanCacheAfterVoice"), false)
                && Private("_playingFile") == null && !File.Exists(sentinel) && !Directory.Exists(session)
                && !Directory.EnumerateFiles(store.CacheDirectory, "voice-*", SearchOption.AllDirectories).Any(),
                "deferred cleanup finishes after cancellation removes files and empty sessions and resets its pending flag");
            Check(!nextServer.Request.IsCompleted, "a voice request rejected during cleanup is never dispatched later");

            var resumed = Speak("清理完成后的语音");
            await nextServer.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Private("_voiceCts") != null && !ReferenceEquals(Private("_voiceCts"), previous),
                "speech can start normally again once deferred cache cleanup completes");
            typeof(MainWindow).GetMethod("OnStop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [window, new RoutedEventArgs()]);
            await resumed.WaitAsync(TimeSpan.FromSeconds(10));
            Check(Private("_playingFile") == null
                && !Directory.EnumerateFiles(store.CacheDirectory, "voice-*", SearchOption.AllDirectories).Any(),
                "cancelling speech resumed after cleanup leaves no playing or partial voice file");
        }
        finally { window.Close(); }
        object? Private(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
        Task Speak(string text) => (Task)typeof(MainWindow).GetMethod("SpeakAsync", BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(string), typeof(bool)], null)!.Invoke(window, [text, true])!;
    }
}
