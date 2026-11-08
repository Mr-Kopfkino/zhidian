using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Qiban.Core;
using Qiban.Desktop;

namespace Qiban.DesktopTests;

/// <summary>Runs the actual WPF window and routed handlers on an STA dispatcher, with isolated files.</summary>
internal static partial class Program
{
    private static int passed;
    private static int result = 1;
    private static ComposerKeyboard? composerKeyboard;

    [STAThread]
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            string workspace = FindWorkspace();
            string root = Path.Combine(workspace, ".build", "ui-test-data", DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            var store = new PortableStore(root);
            store.Initialize();
            var state = new AppState();
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) =>
            {
                Console.Error.WriteLine("FAIL dispatcher: " + e.Exception);
                result = 1;
                e.Handled = true;
                app.Shutdown(1);
            };
            app.Startup += async (_, _) =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = 0,
                        ShowInTaskbar = false,
                        ShowActivated = false
                    };
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await RunAsync(window, store, state);
                    result = 0;
                    Console.WriteLine($"PASS {passed} WPF integration checks. Artifacts: {root}");
                    File.WriteAllText(Path.Combine(root, "result.txt"), $"PASS {passed} checks\n{DateTimeOffset.Now:O}\n", Encoding.UTF8);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("FAIL WPF integration: " + error);
                    result = 1;
                }
                finally
                {
                    window?.Close();
                    app.Shutdown(result);
                }
            };
            app.Run();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL startup: " + error);
            return 1;
        }
        return result;
    }

    private static async Task RunAsync(MainWindow window, PortableStore store, AppState state)
    {
        T Control<T>(string name) where T : FrameworkElement => window.FindName(name) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");
        void Set(string name, string value) => Control<TextBox>(name).Text = value;
        void NamedClick(string name) => Click(Control<Button>(name));
        void TextClick(string text) => Click(FindButton(window, text));
        string Notice() => Control<TextBlock>("NoticeText").Text;

        Check(window.Title == "致电", "application window uses the renamed product title");
        Check(state.Friends[0].Name == "五河琴里" && Control<TextBlock>("FriendName").Text == "五河琴里", "new default character renders as Kotori");
        Check(Control<Grid>("ChatPage").Visibility == Visibility.Visible && Control<Grid>("SettingsPage").Visibility == Visibility.Collapsed, "chat is the initial page");
        Check(Control<StackPanel>("MessagesPanel").Children.Count == 1, "greeting renders without a provider");
        var composer = Control<TextBox>("InputBox");
        Set("InputBox", "输入法候选");
        var imeKey = RouteComposerKey(composer, Key.Enter, imeProcessed: true, previewOnly: true);
        Check(imeKey.Key == Key.ImeProcessed && imeKey.ImeProcessedKey == Key.Enter && !imeKey.Handled
            && composer.Text == "输入法候选" && Notice() == "" && state.Friends[0].Messages.Count == 0,
            "IME-processed keys remain with the input method without submitting the draft");
        Set("InputBox", "第一行");
        composer.CaretIndex = composer.Text.Length;
        var shiftKey = RouteComposerKey(composer, Key.Enter, ModifierKeys.Shift, previewOnly: true);
        Check(!shiftKey.Handled && composer.AcceptsReturn && composer.Text == "第一行" && Notice() == "" && state.Friends[0].Messages.Count == 0,
            "Shift+Enter yields to text editing without submitting the draft");
        var repeatedKey = RouteComposerKey(composer, Key.Enter, repeat: true);
        Check(repeatedKey.Handled && Notice() == "" && state.Friends[0].Messages.Count == 0,
            "held Enter does not submit repeated messages");
        Set("InputBox", "你好");
        Check(RouteComposerKey(composer, Key.Enter).Handled, "Enter is intercepted before the TextBox newline handler");
        Check(state.Friends[0].Messages.Count == 0 && Notice().Contains("模型"), "unconfigured send shows a model setup notice without creating a fake reply");
        Check(Control<TextBox>("InputBox").Text == "你好", "unsent input survives validation");
        Set("InputBox", "");

        NamedClick("SettingsNavButton");
        Check(Control<Grid>("SettingsPage").Visibility == Visibility.Visible && Control<Grid>("ChatPage").Visibility == Visibility.Collapsed, "settings navigation changes page visibility");
        Set("RoleName", "望舒");
        Set("RoleTagline", "聊聊星空与平凡的小事");
        Set("RolePersona", "你叫望舒，是喜欢天文学的朋友。说话简短，温和而直接。");
        Set("RoleGreeting", "晚上好，今天过得怎么样？");
        Set("ModelEndpoint", "http://127.0.0.1:1234/v1");
        Set("ModelName", "local-fixture");
        Set("ModelTemperature", "0.45");
        Set("ContextCount", "12");
        Set("MemoryBudget", "800");
        Set("CacheLimit", "32");
        Set("CacheHours", "2");
        NamedClick("SaveSettingsButton");
        var first = state.Friends[0];
        Check(first.Name == "望舒" && first.Persona.Contains("天文学") && first.Greeting.Contains("晚上好"), "role settings update the actual profile");
        Check(Control<TextBlock>("FriendName").Text == "望舒", "saved role updates chat header");
        var committed = store.Load();
        Check(committed.Settings.Model == "local-fixture" && committed.Settings.Temperature == .45 && committed.Settings.ContextMessages == 12 && committed.Settings.MemoryBudget == 800, "model settings persist to the portable store");
        Set("RoleName", "不得保存的无效提交");
        Set("ModelTemperature", "NaN");
        NamedClick("SaveSettingsButton");
        Check(first.Name == "望舒" && Notice().Contains("温度"), "invalid settings fail before mutating the role");
        Set("RoleName", "望舒");
        Set("ModelTemperature", "0.45");

        TextClick("添加角色");
        Check(state.Friends.Count == 2 && state.SelectedFriendId != first.Id, "add friend creates and selects an independent profile");
        var second = state.Friends.Single(f => f.Id == state.SelectedFriendId);
        Set("RoleName", "青禾");
        Set("RolePersona", "你叫青禾，是热爱植物的朋友，习惯轻快地交流。");
        NamedClick("SaveSettingsButton");
        Control<ListBox>("FriendList").SelectedItem = first;
        Check(Control<TextBox>("RoleName").Text == "望舒" && second.Name == "青禾", "switching friends restores each independent persona");

        NamedClick("MemoryNavButton");
        Check(Control<Grid>("MemoryPage").Visibility == Visibility.Visible && Control<Grid>("SettingsPage").Visibility == Visibility.Collapsed, "memory navigation changes page visibility");
        TextClick("＋ 新建记忆");
        Set("MemoryContent", "我喜欢喝乌龙茶。");
        Control<CheckBox>("MemoryPinned").IsChecked = true;
        Set("MemoryExpiry", DateTime.Today.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        TextClick("保存修改");
        Check(first.Memories.Count == 1 && first.Memories[0].State == "pending", "new memory is pending until user confirmation");
        Check(first.Memories[0].ExpiresAt > DateTimeOffset.Now && first.Memories[0].Pinned, "memory pin and expiration persist");
        TextClick("确认记忆");
        Check(first.Memories[0].State == "approved", "confirm memory enables retrieval");
        Check(Control<TextBlock>("MemoryDetails").Text.Contains("手动录入"), "memory editor shows provenance");

        TextClick("＋ 新建记忆");
        Set("MemoryContent", "我周末想去图书馆。");
        TextClick("保存修改");
        Check(first.Memories.Count == 2 && first.Memories.Count(m => m.State == "pending") == 1, "second candidate stays separate from the confirmed memory");
        Set("MemorySearch", "没有此项内容");
        Check(Control<ListBox>("MemoryList").Items.Count == 0, "memory search handles no matches");
        Set("MemorySearch", "乌龙");
        var list = Control<ListBox>("MemoryList");
        Check(list.Items.Count == 1, "memory search filters by content");
        list.SelectedIndex = 0;
        TextClick("归档");
        Check(first.Memories[0].State == "archived" && MemoryEngine.Retrieve(first, "乌龙茶", 800).Count == 0, "archived and pending memories are excluded from recall");
        TextClick("确认记忆");
        Check(first.Memories[0].State == "approved", "archived memory can be explicitly restored");
        Control<ListBox>("FriendList").SelectedItem = second;
        Check(second.Memories.Count == 0 && Control<ListBox>("MemoryList").Items.Count == 0, "friend switch isolates memory UI and storage");
        Control<ListBox>("FriendList").SelectedItem = first;
        Set("MemorySearch", "");
        window.UpdateLayout();
        Capture(window, Path.Combine(store.Root, "memory.png"));

        await using (var server = new LocalChatServer(holdAfterFirst: false))
        {
            NamedClick("SettingsNavButton");
            Set("ModelEndpoint", server.Endpoint);
            NamedClick("SaveSettingsButton");
            NamedClick("ChatNavButton");
            Set("InputBox", "  \r\n ");
            RouteComposerKey(composer, Key.Enter);
            Check(first.Messages.Count == 0 && !server.Request.IsCompleted, "blank Enter does not contact the model");
            Set("InputBox", "你还记得我喜欢喝什么茶吗？");
            RouteComposerKey(composer, Key.Enter);
            Check(composer.Text.Length == 0 && first.Messages.Count(m => m.Role == "user") == 1,
                "Enter submits one message and clears the draft without inserting a newline");
            Check(!Control<Button>("SendButton").IsEnabled && Control<Button>("StopButton").IsEnabled, "streaming disables duplicate sends and enables cancellation");
            await UntilAsync(() => Control<Button>("SendButton").IsEnabled, "streaming completion");
            var request = await server.Request.WaitAsync(TimeSpan.FromSeconds(10));
            await server.Completion;
            Check(first.Messages.Count == 2 && first.Messages[^1].Text == "记得呀，你喜欢喝乌龙茶。" && first.Messages[^1].Status == "complete", "actual loopback SSE reply updates chat and completes");
            Check(request.Path == "/v1/chat/completions", "UI uses configured compatible chat endpoint");
            using var payload = JsonDocument.Parse(request.Body);
            var messages = payload.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString() ?? "").ToArray();
            Check(messages.Any(m => m.Contains("天文学")), "configured persona reaches the model request");
            Check(messages.Any(m => m.Contains("乌龙茶")) && messages.All(m => !m.Contains("图书馆")), "only approved recalled memory reaches the model request");
            Check(second.Messages.Count == 0, "streamed chat belongs only to its selected friend");
            Check(store.Load().Friends.Single(f => f.Id == first.Id).Messages[^1].Status == "complete", "completed stream is persisted");
        }

        await using (var server = new LocalChatServer(holdAfterFirst: true))
        {
            NamedClick("SettingsNavButton");
            Set("ModelEndpoint", server.Endpoint);
            NamedClick("SaveSettingsButton");
            NamedClick("ChatNavButton");
            Set("InputBox", "测试停止回复");
            NamedClick("SendButton");
            await server.Request.WaitAsync(TimeSpan.FromSeconds(10));
            await UntilAsync(() => first.Messages[^1].Text.Length > 0, "first partial chunk");
            NamedClick("StopButton");
            await UntilAsync(() => Control<Button>("SendButton").IsEnabled, "stream cancellation");
            Check(first.Messages[^1].Status == "interrupted" && first.Messages[^1].Text.Length > 0, "stop preserves partial reply and marks it interrupted");
            Check(!Control<Button>("StopButton").IsEnabled && Control<ListBox>("FriendList").IsEnabled, "cancellation restores navigation and send controls");
        }

        window.Width = 940;
        window.Height = 680;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Check(Control<TextBox>("InputBox").ActualWidth > 200 && Control<TextBox>("InputBox").ActualHeight > 0, "chat composer remains usable at minimum window size");
        foreach (string name in new[] { "ChatNavButton", "MemoryNavButton", "SettingsNavButton" })
        {
            NamedClick(name);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(window.IsMeasureValid && window.IsArrangeValid, name + " measures and arranges at minimum size");
        }
        Capture(window, Path.Combine(store.Root, "settings-minimum.png"));
        window.Width = 1140;
        window.Height = 780;
        NamedClick("ChatNavButton");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(store.Root, "chat.png"));
        await VerifyMemoryRestartAndWriteFailureAsync(Path.Combine(store.Root, "memory-reliability"));
        await VerifyInitiativeAsync(Path.Combine(store.Root, "initiative"));
        VerifyActivitySensor();
        await VerifyChatBackgroundAsync(Path.Combine(store.Root, "background"));
        await VerifyWindowChromeAsync(Path.Combine(store.Root, "window-chrome"));
        await VerifyInterfaceThemeAsync(Path.Combine(store.Root, "interface-theme"));
        await VerifyFreshPortableStartupAsync(Path.Combine(store.Root, "fresh-portable-startup"));
        await VerifyRoleVoiceAsync(Path.Combine(store.Root, "role-voice"));
        await VerifyEmbeddedVoiceSettingsAsync(Path.Combine(store.Root, "embedded-voice-settings"));
        await VerifyDeferredVoiceCacheCleanupAsync(Path.Combine(store.Root, "voice-cache-cleanup"));
        await VerifyJapaneseChatAsync(Path.Combine(store.Root, "japanese-chat"));
        await VerifyInitiativeTriggersAsync(Path.Combine(store.Root, "initiative-triggers"));
        await VerifyActivityTriggersAsync(Path.Combine(store.Root, "activity-triggers"));
        await VerifyMemoryLearningAsync(Path.Combine(store.Root, "memory-learning"));
    }

    // Exercise both routed phases, including TextBox's class handler, rather than
    // invoking the application's event callback directly. No system keys are injected.
    private static KeyEventArgs RouteComposerKey(TextBox composer, Key key, ModifierKeys modifiers = ModifierKeys.None,
        bool imeProcessed = false, bool repeat = false, bool previewOnly = false)
    {
        composerKeyboard ??= new ComposerKeyboard();
        composerKeyboard.TestModifiers = modifiers;
        var args = new KeyEventArgs(composerKeyboard, PresentationSource.FromVisual(composer)!, 0, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        if (imeProcessed) typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, null);
        if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, [true]);
        composer.RaiseEvent(args);
        if (!args.Handled && !previewOnly)
        {
            args.RoutedEvent = Keyboard.KeyDownEvent;
            composer.RaiseEvent(args);
        }
        return args;
    }

    private sealed class ComposerKeyboard() : KeyboardDevice(InputManager.Current)
    {
        public ModifierKeys TestModifiers { get; set; }
        protected override KeyStates GetKeyStatesFromSystem(Key key) =>
            TestModifiers.HasFlag(ModifierKeys.Shift) && (key is Key.LeftShift or Key.RightShift) ? KeyStates.Down : KeyStates.None;
    }

    private static async Task VerifyRoleVoiceAsync(string root)
    {
        var store = new PortableStore(root);
        store.Initialize();
        var state = new AppState();
        state.Friends[0].Name = "望舒";
        store.Save(state);
        MainWindow? window = null;
        try
        {
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            var first = state.Friends[0];
            SetVoice("A", "OpenAI-compatible", "http://127.0.0.1:19880/v1/audio/speech", "voice-key-A");
            Save();
            Check(first.Voice.Enabled && first.Voice.AutoPlay && first.Voice.Model == "voice-model-A"
                && first.Voice.Voice == "voice-A" && first.Voice.ReferenceAudio == "D:/fixture/A.wav"
                && first.Voice.GptWeights == "D:/fixture/A.ckpt" && first.Voice.SovitsWeights == "D:/fixture/A.pth"
                && SecretVault.Unprotect(first.Voice.ProtectedApiKey) == "voice-key-A",
                "role voice settings and protected credentials save to the selected character");
            string firstSaved = JsonSerializer.Serialize(first.Voice);

            Click(FindButton(window, "添加角色"));
            var second = state.Friends.Single(f => f.Id == state.SelectedFriendId);
            Check(!ReferenceEquals(first.Voice, second.Voice) && !second.Voice.Enabled
                && second.Voice.ProtectedApiKey == "" && second.Voice.ReferenceAudio == ""
                && second.Voice.GptWeights == "" && second.Voice.SovitsWeights == "",
                "new characters receive fresh voice settings instead of inheriting another character's model");
            Control<TextBox>("RoleName").Text = "青禾";
            SetVoice("B", "GPT-SoVITS 外接", "http://127.0.0.1:19881/tts", "voice-key-B");
            Save();
            Check(JsonSerializer.Serialize(first.Voice) == firstSaved && second.Voice.Model == "voice-model-B"
                && second.Voice.ReferenceText == "参考文本 B" && second.Voice.Language == "zh"
                && SecretVault.Unprotect(second.Voice.ProtectedApiKey) == "voice-key-B",
                "saving a second voice leaves every field and credential of the first role unchanged");
            string secondSaved = JsonSerializer.Serialize(second.Voice);
            Select(first);
            Check(Control<TextBox>("VoiceModel").Text == "voice-model-A"
                && Control<TextBox>("VoiceName").Text == "voice-A"
                && Control<TextBox>("VoiceEndpoint").Text == "http://127.0.0.1:19880/v1/audio/speech"
                && Control<TextBox>("VoiceReference").Text == "D:/fixture/A.wav"
                && Control<TextBox>("VoiceGptWeights").Text == "D:/fixture/A.ckpt"
                && Control<TextBox>("VoiceTemplate").Text.Contains("voice-A")
                && Control<TextBlock>("VoiceCharacterName").Text.Contains(first.Name)
                && Control<PasswordBox>("VoiceKey").Password.Length == 0,
                "switching roles reloads the complete matching voice form without revealing its key");
            Control<TextBox>("VoiceName").Text = "voice-A-updated";
            Control<PasswordBox>("VoiceKey").Password = "-";
            Save();
            Check(first.Voice.ProtectedApiKey == "" && first.Voice.Voice == "voice-A-updated"
                && JsonSerializer.Serialize(second.Voice) == secondSaved,
                "editing a voice or clearing its key affects only the selected role");
            Control<PasswordBox>("VoiceKey").Password = "voice-key-A";
            Save();
            firstSaved = JsonSerializer.Serialize(first.Voice);
            window.Close();
            window = null;
            store = new PortableStore(root);
            store.Initialize();
            state = store.Load();
            first = state.Friends[0]; second = state.Friends[1];
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            Check(JsonSerializer.Serialize(first.Voice) == firstSaved
                && JsonSerializer.Serialize(second.Voice) == secondSaved
                && !ReferenceEquals(first.Voice, second.Voice),
                "independent voice configurations and encrypted keys survive a full store and window restart");
            var voiceTab = Descendants(window).OfType<TabItem>().Single(t => Equals(t.Header, "角色声音"));
            voiceTab.IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(root, "role-voice.png"));

            await using (var server = new LocalVoiceServer())
            await using (var secondServer = new LocalVoiceServer())
            {
                Select(first);
                Control<TextBox>("VoiceEndpoint").Text = server.BaseAddress + "/v1/audio/speech";
                Save();
                var speech = Speak("角色 A 的朗读内容");
                var request = await server.Request.WaitAsync(TimeSpan.FromSeconds(10));
                using var body = JsonDocument.Parse(request.Body);
                Check(request.Path == "/v1/audio/speech" && request.Authorization == "Bearer voice-key-A"
                    && body.RootElement.GetProperty("model").GetString() == "voice-model-A"
                    && body.RootElement.GetProperty("voice").GetString() == "voice-A-updated"
                    && body.RootElement.GetProperty("input").GetString() == "角色 A 的朗读内容",
                    "actual speech synthesis uses the selected role's endpoint, model, voice and credential");
                await server.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
                var previousCancellation = Private("_voiceCts");
                Select(second);
                Control<TextBox>("VoiceEndpoint").Text = secondServer.BaseAddress + "/tts";
                // Dedicated preloaded GPT-SoVITS fixture; weight switching itself has network tests.
                Control<TextBox>("VoiceGptWeights").Text = "";
                Control<TextBox>("VoiceSovitsWeights").Text = "";
                Save();
                var nextSpeech = Speak("角色 B 的朗读内容");
                var nextCancellation = Private("_voiceCts");
                await speech.WaitAsync(TimeSpan.FromSeconds(10));
                Check(Private("_playingFile") == null
                    && Control<TextBox>("VoiceReference").Text == "D:/fixture/B.wav",
                    "switching roles cancels in-flight speech without playing the previous character's audio");
                Check(nextCancellation != null && !ReferenceEquals(previousCancellation, nextCancellation)
                    && ReferenceEquals(Private("_voiceCts"), nextCancellation),
                    "an older cancelled operation cannot clear a newer role's cancellation handle");
                var nextRequest = await secondServer.Request.WaitAsync(TimeSpan.FromSeconds(10));
                using var nextBody = JsonDocument.Parse(nextRequest.Body);
                Check(nextRequest.Path == "/tts" && nextRequest.Authorization == "Bearer voice-key-B"
                    && nextBody.RootElement.GetProperty("ref_audio_path").GetString() == "D:/fixture/B.wav"
                    && nextBody.RootElement.GetProperty("prompt_text").GetString() == "参考文本 B"
                    && nextBody.RootElement.GetProperty("text_lang").GetString() == "zh",
                    "the other role sends its own GPT-SoVITS reference audio, text, language and credential");
                await secondServer.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Click(Control<Button>("StopButton"));
                await nextSpeech.WaitAsync(TimeSpan.FromSeconds(10));
                Check(Private("_playingFile") == null && first.Voice.Provider == "OpenAI-compatible"
                    && first.Voice.Voice == "voice-A-updated" && first.Voice.GptWeights == "D:/fixture/A.ckpt"
                    && !Directory.EnumerateFiles(store.CacheDirectory, "voice-*", SearchOption.AllDirectories).Any(),
                    "stopping replacement speech clears partial audio and preserves the other role's model and weights");
            }

            await using (var voiceServer = new LocalVoiceServer())
            await using (var chatServer = new LocalChatServer(holdAfterFirst: false))
            {
                Select(first);
                Control<TextBox>("VoiceEndpoint").Text = voiceServer.BaseAddress + "/v1/audio/speech";
                Control<TextBox>("ModelEndpoint").Text = chatServer.Endpoint;
                Control<TextBox>("ModelName").Text = "role-voice-fixture";
                Save();
                Click(Control<Button>("ChatNavButton"));
                Control<TextBox>("InputBox").Text = "测试角色 A 自动朗读";
                Click(Control<Button>("SendButton"));
                var request = await voiceServer.Request.WaitAsync(TimeSpan.FromSeconds(10));
                using var body = JsonDocument.Parse(request.Body);
                Check(first.Messages.Last().Status == "complete" && request.Authorization == "Bearer voice-key-A"
                    && body.RootElement.GetProperty("voice").GetString() == "voice-A-updated"
                    && body.RootElement.GetProperty("input").GetString() == first.Messages.Last().Text,
                    "automatic reading after a real chat response uses that character's individual voice");
                await voiceServer.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Select(second);
                await UntilAsync(() => Private("_voiceCts") == null, "automatic voice cancellation after friend switch");
                Check(Private("_playingFile") == null
                    && !Directory.EnumerateFiles(store.CacheDirectory, "voice-*", SearchOption.AllDirectories).Any(),
                    "switching during automatic reading prevents stale audio playback and leaves no voice cache");
            }
        }
        finally { window?.Close(); }

        T Control<T>(string name) where T : FrameworkElement => window?.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing role voice control: {name}");
        void Save() => Click(Control<Button>("SaveSettingsButton"));
        void Select(FriendProfile friend) => Control<ListBox>("FriendList").SelectedItem = friend;
        object? Private(string name) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
        Task Speak(string text) => (Task)typeof(MainWindow).GetMethod("SpeakAsync", BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(string), typeof(bool)], null)!.Invoke(window, [text, true])!;
        MainWindow Open()
        {
            var opened = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            opened.Show();
            return opened;
        }
        void SetVoice(string suffix, string provider, string endpoint, string key)
        {
            Control<CheckBox>("VoiceEnabled").IsChecked = true;
            Control<CheckBox>("VoiceAutoPlay").IsChecked = true;
            var providers = Control<ComboBox>("VoiceProvider");
            providers.SelectedItem = providers.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Content, provider));
            foreach (var (name, value) in new[]
            {
                ("VoiceEndpoint", endpoint), ("VoiceModel", "voice-model-" + suffix), ("VoiceName", "voice-" + suffix),
                ("VoiceLanguage", suffix == "A" ? "ja" : "zh"), ("VoiceReference", "D:/fixture/" + suffix + ".wav"),
                ("VoiceReferenceText", "参考文本 " + suffix), ("VoiceGptWeights", "D:/fixture/" + suffix + ".ckpt"),
                ("VoiceSovitsWeights", "D:/fixture/" + suffix + ".pth"),
                ("VoiceTemplate", "{\"text\":\"{{text}}\",\"voice\":\"voice-" + suffix + "\"}")
            }) Control<TextBox>(name).Text = value;
            Control<PasswordBox>("VoiceKey").Password = key;
        }
    }

    private static async Task VerifyWindowChromeAsync(string root)
    {
        var store = new PortableStore(root);
        store.Initialize();
        var state = new AppState();
        state.Friends[0].Messages = [new() { Role = "user", Text = "窗口样式已经统一了吗？" },
            new() { Role = "assistant", Text = "已经调整好了。试试拖动窗口，或者把它最大化。" }];
        store.Save(state);
        var window = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            Width = 1140, Height = 780, ShowActivated = false, ShowInTaskbar = false
        };
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(window.WindowStyle == WindowStyle.None && !window.AllowsTransparency
                && window.ResizeMode == ResizeMode.CanResize && WindowChrome.GetWindowChrome(window) != null,
                "custom frame retains native resizable chrome without a transparent layered window");
            var title = Control<Grid>("WindowTitleBar");
            var frame = Control<Border>("WindowFrame");
            var close = Control<Button>("CloseWindowButton");
            Check(Hit(title.PointToScreen(new Point(title.ActualWidth / 2, title.ActualHeight / 2))) == 2
                && Hit(frame.PointToScreen(new Point(1, frame.ActualHeight / 2))) == 10
                && Hit(close.PointToScreen(new Point(close.ActualWidth / 2, close.ActualHeight / 2))) == 1,
                "native hit testing exposes draggable caption, resize edge and clickable close control");
            Capture(window, Path.Combine(root, "window-frame.png"));

            // Maximize tests can move an offscreen window onto a real monitor. Keep this fixture
            // invisible throughout those native state transitions, without activating it.
            window.Opacity = 0;
            Click(Control<Button>("MinimizeWindowButton"));
            await UntilAsync(() => window.WindowState == WindowState.Minimized, "custom minimize command");
            Check(window.WindowState == WindowState.Minimized, "custom minimize button invokes native window minimization");
            SystemCommands.RestoreWindow(window);
            await UntilAsync(() => window.WindowState == WindowState.Normal, "restore minimized fixture");
            Click(Control<Button>("MaximizeWindowButton"));
            await UntilAsync(() => window.WindowState == WindowState.Maximized, "custom maximize command");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Control<TextBlock>("MaximizeGlyph").Text == "\uE923"
                && AutomationProperties.GetName(Control<Button>("MaximizeWindowButton")) == "还原窗口",
                "maximized state exposes the restore glyph and accessible restore action");
            window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            var monitor = ChromeMonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            var monitorInfo = new ChromeMonitorInfo { Size = Marshal.SizeOf<ChromeMonitorInfo>() };
            ChromeRect windowBounds = default;
            bool boundsAvailable = monitor != IntPtr.Zero && GetChromeMonitorInfo(monitor, ref monitorInfo)
                && GetChromeWindowRect(handle, out windowBounds);
            var work = monitorInfo.Work;
            Check(boundsAvailable && Math.Abs(windowBounds.Left - work.Left) <= 2
                && Math.Abs(windowBounds.Top - work.Top) <= 2 && Math.Abs(windowBounds.Right - work.Right) <= 2
                && Math.Abs(windowBounds.Bottom - work.Bottom) <= 2
                && InWorkArea(close) && InWorkArea(Control<Button>("SendButton")),
                $"maximized frame matches its monitor work area and keeps caption/chat actions visible " +
                $"(window {windowBounds.Left},{windowBounds.Top},{windowBounds.Right},{windowBounds.Bottom}; " +
                $"work {work.Left},{work.Top},{work.Right},{work.Bottom})");
            Click(Control<Button>("MaximizeWindowButton"));
            await UntilAsync(() => window.WindowState == WindowState.Normal, "custom restore command");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Math.Abs(window.Width - 1140) < 1 && Math.Abs(window.Height - 780) < 1
                && Control<TextBlock>("MaximizeGlyph").Text == "\uE922"
                && AutomationProperties.GetName(Control<Button>("MaximizeWindowButton")) == "最大化窗口",
                "restore returns the previous window size and restores the accessible maximize action");

            window.Left = -20000;
            window.Top = 0;
            window.Width = 940;
            window.Height = 680;
            window.Opacity = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var composer = Control<TextBox>("InputBox");
            var chat = Control<Grid>("ChatPage");
            var titleBottom = title.TranslatePoint(new Point(0, title.ActualHeight), window).Y;
            Check(chat.TranslatePoint(new Point(0, 0), window).Y >= titleBottom - 1
                && composer.ActualWidth > 200 && composer.ActualHeight > 0
                && Inside(composer) && Inside(close) && Inside(Control<Button>("SendButton")),
                "minimum window layout keeps title controls and the chat composer fully inside the frame");
            Capture(window, Path.Combine(root, "window-frame-minimum.png"));
            state.Friends[0].Memories.Add(new MemoryItem { Content = "窗口关闭测试记忆", State = "approved" });
            Click(close);
            await UntilAsync(() => closed, "custom close command");
            Check(store.Load().Friends[0].Memories.Any(m => m.Content == "窗口关闭测试记忆")
                && store.Load().Friends[0].Messages.Count == 2,
                "custom close follows the existing shutdown path and persists conversation and memory");

            bool InWorkArea(FrameworkElement element)
            {
                Point topLeft = element.PointToScreen(new Point(0, 0));
                Point bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                return element.ActualWidth > 0 && element.ActualHeight > 0
                    && topLeft.X >= work.Left - 2 && topLeft.Y >= work.Top - 2
                    && bottomRight.X <= work.Right + 2 && bottomRight.Y <= work.Bottom + 2;
            }
        }
        finally { if (!closed) window.Close(); }

        T Control<T>(string name) where T : FrameworkElement => window.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing chrome control: {name}");
        bool Inside(FrameworkElement element)
        {
            Point origin = element.TranslatePoint(new Point(0, 0), window);
            return origin.X >= -1 && origin.Y >= -1
                && origin.X + element.ActualWidth <= window.ActualWidth + 1
                && origin.Y + element.ActualHeight <= window.ActualHeight + 1;
        }
        int Hit(Point point)
        {
            int x = checked((int)Math.Round(point.X)), y = checked((int)Math.Round(point.Y));
            var coordinates = new IntPtr((long)((uint)(ushort)x | ((uint)(ushort)y << 16)));
            return SendChromeMessage(new WindowInteropHelper(window).Handle, 0x0084 /* WM_NCHITTEST */,
                IntPtr.Zero, coordinates).ToInt32();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendChromeMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct ChromeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChromeMonitorInfo
    {
        public int Size;
        public ChromeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow", ExactSpelling = true)]
    private static extern IntPtr ChromeMonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetChromeMonitorInfo(IntPtr monitor, ref ChromeMonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowRect", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetChromeWindowRect(IntPtr window, out ChromeRect bounds);

    private static async Task VerifyMemoryRestartAndWriteFailureAsync(string root)
    {
        // Each window uses real persistence and handlers, independent of the chat fixture above.
        var store = new PortableStore(root);
        store.Initialize();
        var state = store.Load();
        store.Save(state);
        string sourceId = Guid.NewGuid().ToString("N");
        var sourceTime = DateTimeOffset.Parse("2026-11-01T08:30:00+08:00", CultureInfo.InvariantCulture);
        state.Friends[0].Messages.Add(new ChatMessage { Id = sourceId, Text = "记住：我喜欢无糖乌龙茶。", CreatedAt = sourceTime });
        state.Friends[0].Memories.Add(new MemoryItem
        {
            Content = "我喜欢无糖乌龙茶。", SourceMessageId = sourceId, CreatedAt = sourceTime,
            State = "pending", Category = "偏好"
        });
        store.Save(state);
        MainWindow? current = null;
        try
        {
            current = Open(store, state);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("MemoryNavButton"));
            Control<ListBox>("MemoryList").SelectedIndex = 0;
            Click(FindButton(current, "确认记忆"));
            string memoryId = state.Friends[0].Memories.Single().Id;
            var confirmedTime = state.Friends[0].Memories.Single().UpdatedAt;
            Check(state.Friends[0].Memories.Single().State == "approved", "memory confirmation commits through the actual editor");
            Check(MemoryEngine.Retrieve(state.Friends[0], "无糖乌龙茶", 800).Single().Id == memoryId,
                "confirmed memory is retrievable before restart");
            current.Close();
            current = null;

            store = new PortableStore(root);
            store.Initialize();
            state = store.Load();
            current = Open(store, state);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("MemoryNavButton"));
            Control<ListBox>("MemoryList").SelectedIndex = 0;
            var restored = state.Friends[0].Memories.Single();
            Check(restored.Id == memoryId && restored.State == "approved" && restored.Content == "我喜欢无糖乌龙茶。",
                "confirmed memory survives closing and opening a new window and store");
            Check(restored.SourceMessageId == sourceId && restored.CreatedAt == sourceTime && restored.UpdatedAt == confirmedTime,
                "memory evidence and timestamps survive restart unchanged");
            Check(Control<TextBox>("MemoryContent").Text == restored.Content && Control<TextBlock>("MemoryDetails").Text.Contains("无糖乌龙茶"),
                "restarted memory editor displays persisted content and original evidence");
            Check(MemoryEngine.Retrieve(state.Friends[0], "无糖乌龙茶", 800).Single().Id == memoryId,
                "memory retrieval remains consistent after restart");

            const string draft = "我喜欢无糖乌龙茶，下午不喝咖啡。";
            Control<TextBox>("MemoryContent").Text = draft;
            Control<CheckBox>("MemoryPinned").IsChecked = true;
            Control<TextBox>("MemoryExpiry").Text = DateTime.Today.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string draftExpiry = Control<TextBox>("MemoryExpiry").Text;
            string statePath = Path.Combine(store.DataDirectory, "state.json");
            string backupPath = Path.Combine(store.DataDirectory, "state.backup.json");
            Check(File.Exists(backupPath), "latest recovery snapshot exists before a memory edit");
            string committedBytes = File.ReadAllText(statePath);
            int selectionEvents = 0;
            Control<ListBox>("FriendList").SelectionChanged += (_, _) => selectionEvents++;
            using (var lockedBackup = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Click(FindButton(current, "保存修改"));
                await Task.Delay(250);
                Check(selectionEvents <= 4, "persistent save failure does not trigger an automatic selection/save loop");
                Check(state.Friends[0].Memories.Single().Content == "我喜欢无糖乌龙茶。"
                    && !state.Friends[0].Memories.Single().Pinned && state.Friends[0].Memories.Single().ExpiresAt == null,
                    "failed memory save rolls the in-memory record back to its committed version");
                Check(File.ReadAllText(statePath) == committedBytes, "failed first snapshot replacement leaves primary data unchanged");
                Check(Control<TextBox>("MemoryContent").Text == draft && Control<CheckBox>("MemoryPinned").IsChecked == true
                    && Control<TextBox>("MemoryExpiry").Text == draftExpiry,
                    "failed memory save preserves editable content, pin and expiration drafts");
                Check(!string.IsNullOrWhiteSpace(Control<TextBlock>("NoticeText").Text), "save failure is visible instead of reporting success");
            }
            Click(FindButton(current, "保存修改"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(state.Friends[0].Memories.Count == 1 && state.Friends[0].Memories[0].Id == memoryId
                && state.Friends[0].Memories[0].Content == draft && state.Friends[0].Memories[0].Pinned,
                "unlocking storage allows the retained draft to save without duplicating its memory");
            Check(store.Load().Friends[0].Memories.Single().Content == draft,
                "retry commits the retained draft to persistent storage");
            current.Close();
            current = null;

            store = new PortableStore(root);
            store.Initialize();
            state = store.Load();
            current = Open(store, state);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("MemoryNavButton"));
            Control<ListBox>("MemoryList").SelectedIndex = 0;
            Check(Control<TextBox>("MemoryContent").Text == draft && Control<CheckBox>("MemoryPinned").IsChecked == true
                && Control<TextBox>("MemoryExpiry").Text == draftExpiry,
                "the successful retry and all memory flags survive a second restart");
            Check(MemoryEngine.Retrieve(state.Friends[0], "咖啡", 800).Single().Id == memoryId,
                "updated confirmed memory is recalled after restart");
        }
        finally { current?.Close(); }

        T Control<T>(string name) where T : FrameworkElement => current?.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing reliability control: {name}");
        static MainWindow Open(PortableStore portableStore, AppState appState)
        {
            var window = new MainWindow(portableStore, appState, enableInitiative: false, enableMemoryLearning: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowActivated = false, ShowInTaskbar = false
            };
            window.Show();
            return window;
        }
    }

    private static async Task VerifyInitiativeAsync(string root)
    {
        Check(DesktopActivityMonitor.DescribeProcess("CHROME.exe") == new ActivityObservation("CHROME", "浏览器")
            && DesktopActivityMonitor.DescribeProcess("vlc")?.Category == "视频播放器"
            && DesktopActivityMonitor.DescribeProcess("eldenring")?.Category == "游戏",
            "foreground process names classify browsers, players and games without reading window content");
        Check(DesktopActivityMonitor.DescribeProcess("explorer") == null
            && DesktopActivityMonitor.DescribeProcess("Zhidian") == null
            && DesktopActivityMonitor.DescribeProcess("Laidian") == null
            && DesktopActivityMonitor.DescribeProcess("LockApp") == null
            && DesktopActivityMonitor.DescribeProcess("invalid\nname") == null,
            "activity classification excludes shell, companion, lock screen and invalid names");
        Check(DesktopActivityMonitor.DescribeProcess("UnknownFixture") == new ActivityObservation("UnknownFixture", "应用"),
            "unrecognized foreground apps remain eligible without inventing a category");

        var store = new PortableStore(root);
        store.Initialize();
        var state = new AppState();
        var friend = state.Friends[0];
        friend.Persona = "你是喜欢天文学的朋友，说话简短、克制而温和。";
        friend.Messages = [new() { Role = "user", Text = "你记得我喜欢喝什么茶吗？" },
            new() { Role = "assistant", Text = "记得，我们之前谈过茶。" }];
        friend.Memories = [new() { State = "approved", Pinned = true, Content = "我喜欢无糖乌龙茶。" },
            new() { State = "pending", Content = "待确认的私人记忆标记" }];
        // Reproduce an actual pre-feature snapshot: all former required fields are present,
        // but the optional proactive settings, cached profile and trigger tags do not exist.
        var legacy = JsonSerializer.SerializeToNode(state)!.AsObject();
        legacy["Settings"]!.AsObject().Remove("Initiative");
        foreach (var oldFriend in legacy["Friends"]!.AsArray())
        {
            oldFriend!.AsObject().Remove("InitiativeProfile");
            oldFriend.AsObject().Remove("InitiativeProfileKey");
            foreach (var message in oldFriend["Messages"]!.AsArray()) message!.AsObject().Remove("Trigger");
        }
        string legacyJson = legacy.ToJsonString();
        File.WriteAllText(Path.Combine(store.DataDirectory, "state.json"), legacyJson);
        File.WriteAllText(Path.Combine(store.DataDirectory, "state.backup.json"), legacyJson);
        state = store.Load();
        MainWindow? window = null;
        try
        {
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            Check(Control<CheckBox>("InitiativeIdleEnabled").IsChecked == true
                && Control<CheckBox>("InitiativeActivityEnabled").IsChecked == true
                && Control<TextBox>("InitiativeMaxUnanswered").Text == "3",
                "legacy state loads new proactive defaults into the actual settings UI");
            Control<CheckBox>("InitiativeIdleEnabled").IsChecked = false;
            Control<CheckBox>("InitiativeActivityEnabled").IsChecked = true;
            Control<TextBox>("InitiativeMaxUnanswered").Text = "5";
            Control<TextBox>("ModelName").Text = "initiative-fixture";
            Click(Control<Button>("SaveSettingsButton"));
            var saved = store.Load();
            Check(!saved.Settings.Initiative.IdleEnabled && saved.Settings.Initiative.ActivityEnabled
                && saved.Settings.Initiative.MaxUnanswered == 5,
                "proactive toggles and unanswered limit save through the actual settings handler");
            Check(saved.Friends[0].Messages.Count == 2 && saved.Friends[0].Messages.All(m => m.Trigger == "")
                && saved.Friends[0].Memories.Count == 2,
                "saving new settings preserves legacy conversation and long-term memories");
            string roleName = state.Friends[0].Name;
            Control<TextBox>("RoleName").Text = "invalid-limit-must-not-commit";
            Control<TextBox>("InitiativeMaxUnanswered").Text = "0";
            Click(Control<Button>("SaveSettingsButton"));
            Check(state.Friends[0].Name == roleName && state.Settings.Initiative.MaxUnanswered == 5
                && Control<TextBlock>("NoticeText").Text.Contains("连续未回复"),
                "invalid proactive limit is rejected before changing other settings");
            window.Close();
            window = null;
            store = new PortableStore(root);
            store.Initialize();
            state = store.Load();
            friend = state.Friends[0];
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            Check(Control<CheckBox>("InitiativeIdleEnabled").IsChecked == false
                && Control<CheckBox>("InitiativeActivityEnabled").IsChecked == true
                && Control<TextBox>("InitiativeMaxUnanswered").Text == "5",
                "proactive preferences survive a new store and WPF window");

            var initiativeTab = Descendants(window).OfType<TabItem>().Single(t => t.Header?.ToString() == "主动聊天");
            initiativeTab.IsSelected = true;
            window.Width = 940;
            window.Height = 680;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Capture(window, Path.Combine(store.Root, "initiative-settings.png"));
            window.Width = 1140;
            window.Height = 780;
            Descendants(window).OfType<TabItem>().Single(t => t.Header?.ToString() == "角色").IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            const string draft = "这是一条尚未发送的草稿";
            Control<TextBox>("InputBox").Text = draft;
            int userCount = friend.Messages.Count(m => m.Role == "user");
            await using (var server = new LocalChatServer(holdAfterFirst: false))
            {
                ConfigureEndpoint(server.Endpoint);
                var task = SendInitiative(new("activity", new("vlc", "视频播放器"), 0));
                Check(Control<Button>("SendButton").IsEnabled && Control<Button>("StopButton").IsEnabled,
                    "proactive generation keeps user send available and offers stop");
                var recorded = await server.Request.WaitAsync(TimeSpan.FromSeconds(10));
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                await server.Completion;
                using var payload = JsonDocument.Parse(recorded.Body);
                var messages = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                var contents = messages.Select(m => m.GetProperty("content").GetString() ?? "").ToArray();
                Check(friend.Messages.Count == 3 && friend.Messages[^1].Role == "assistant"
                    && friend.Messages[^1].Trigger == "activity" && friend.Messages[^1].Status == "complete"
                    && friend.Messages[^1].Text == "记得呀，你喜欢喝乌龙茶。",
                    "activity trigger streams a real provider response as an assistant message");
                Check(friend.Messages.Count(m => m.Role == "user") == userCount
                    && messages.Count(m => m.GetProperty("role").GetString() == "user") == userCount,
                    "proactive generation does not fabricate a user message in history or request");
                Check(contents.Any(x => x.Contains("天文学")) && contents.Any(x => x.Contains("无糖乌龙茶"))
                    && contents.All(x => !x.Contains("待确认的私人记忆标记") && !x.Contains(draft)),
                    "proactive request includes the persona and confirmed memory without draft or pending-memory leakage");
                string instruction = contents[^1];
                using var observation = JsonDocument.Parse(instruction[(instruction.LastIndexOf('\n') + 1)..]);
                Check(observation.RootElement.GetProperty("trigger").GetString() == "activity"
                    && observation.RootElement.GetProperty("idleMinutes").GetDouble() == 0
                    && observation.RootElement.GetProperty("activity").GetProperty("processName").GetString() == "vlc",
                    "activity prompt carries the current process even with no idle interval");
                Check(Control<TextBox>("InputBox").Text == draft
                    && Control<Grid>("SettingsPage").Visibility == Visibility.Visible
                    && Control<Grid>("ChatPage").Visibility == Visibility.Collapsed,
                    "background proactive output preserves draft and selected page");
                var committed = store.Load();
                Check(committed.Friends[0].Messages[^1].Trigger == "activity"
                    && committed.Friends[0].Messages[^1].Status == "complete"
                    && committed.Friends[0].Memories.Count == 2,
                    "proactive message persists while application observation creates no long-term memory");
            }
            await using (var server = new LocalChatServer(holdAfterFirst: true))
            {
                ConfigureEndpoint(server.Endpoint);
                var task = SendInitiative(new("idle", null, 12));
                await server.Request.WaitAsync(TimeSpan.FromSeconds(10));
                await UntilAsync(() => friend.Messages[^1].Text.Length > 0, "proactive partial chunk");
                Click(Control<Button>("StopButton"));
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(friend.Messages[^1].Trigger == "idle" && friend.Messages[^1].Status == "interrupted"
                    && friend.Messages[^1].Text.Length > 0 && friend.Messages.Count(m => m.Role == "user") == userCount,
                    "stopping proactive output preserves partial text without creating a user turn");
                Check(Control<TextBox>("InputBox").Text == draft && Control<Button>("SendButton").IsEnabled
                    && !Control<Button>("StopButton").IsEnabled,
                    "proactive cancellation preserves the composer and restores controls");
            }
            await using (var server = new LocalChatServer(holdAfterFirst: false, holdBeforeFirst: true))
            {
                ConfigureEndpoint(server.Endpoint);
                int before = friend.Messages.Count;
                var task = SendInitiative(new("activity", new("chrome", "浏览器"), 0));
                await server.Request.WaitAsync(TimeSpan.FromSeconds(10));
                Click(Control<Button>("StopButton"));
                await task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(friend.Messages.Count == before && store.Load().Friends[0].Messages.Count == before
                    && Control<StackPanel>("MessagesPanel").Children.Count == before,
                    "cancelling before proactive content leaves no empty message in UI or storage");
            }
            await using (var proactiveServer = new LocalChatServer(holdAfterFirst: true))
            await using (var replyServer = new LocalChatServer(holdAfterFirst: false))
            {
                ConfigureEndpoint(proactiveServer.Endpoint);
                int before = friend.Messages.Count;
                var proactiveTask = SendInitiative(new("activity", new("mpv", "视频播放器"), 0));
                var tracking = typeof(MainWindow).GetField("_initiativeTask", BindingFlags.NonPublic | BindingFlags.Instance)!;
                // Mirror the scheduler's task tracking while keeping desktop monitoring disabled.
                tracking.SetValue(window, proactiveTask);
                try
                {
                    await proactiveServer.Request.WaitAsync(TimeSpan.FromSeconds(10));
                    await UntilAsync(() => friend.Messages[^1].Text.Length > 0, "preemptible proactive chunk");
                    state.Settings.Endpoint = replyServer.Endpoint;
                    const string userMessage = "先聊我现在的问题。";
                    Control<TextBox>("InputBox").Text = userMessage;
                    Click(Control<Button>("SendButton"));
                    var recorded = await replyServer.Request.WaitAsync(TimeSpan.FromSeconds(10));
                    Check(!Control<Button>("SendButton").IsEnabled && Control<Button>("StopButton").IsEnabled,
                        "user preemption switches from interruptible proactive output to a normal busy reply");
                    await proactiveTask.WaitAsync(TimeSpan.FromSeconds(10));
                    await UntilAsync(() => !Control<Button>("StopButton").IsEnabled, "reply after proactive preemption");
                    await replyServer.Completion;
                    Check(friend.Messages.Count == before + 3
                        && friend.Messages[before].Trigger == "activity" && friend.Messages[before].Status == "interrupted"
                        && friend.Messages[before].Text == "记得呀，"
                        && friend.Messages[before + 1].Role == "user" && friend.Messages[before + 1].Text == userMessage
                        && friend.Messages[^1].Role == "assistant" && friend.Messages[^1].Trigger == ""
                        && friend.Messages[^1].Status == "complete" && friend.Messages[^1].Text == "记得呀，你喜欢喝乌龙茶。",
                        "user send cancels the proactive stream then records one user turn and a separate completed reply in order");
                    using var payload = JsonDocument.Parse(recorded.Body);
                    var context = payload.RootElement.GetProperty("messages").EnumerateArray().ToArray();
                    Check(context[^1].GetProperty("role").GetString() == "user"
                        && context[^1].GetProperty("content").GetString() == userMessage
                        && friend.Messages.Count(m => m.Role == "user") == userCount + 1
                        && Control<TextBox>("InputBox").Text.Length == 0,
                        "only the actual sent draft becomes the next model request and clears the composer");
                    var committed = store.Load().Friends[0].Messages;
                    Check(committed.Count == friend.Messages.Count && committed[before].Status == "interrupted"
                        && committed[^1].Status == "complete" && committed[^1].Text == friend.Messages[^1].Text,
                        "preempted and replacement responses persist without mixed or duplicated buffers");
                }
                finally { tracking.SetValue(window, null); }
            }
        }
        finally { window?.Close(); }

        T Control<T>(string name) where T : FrameworkElement => window?.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing proactive control: {name}");
        MainWindow Open()
        {
            var opened = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowActivated = false, ShowInTaskbar = false
            };
            opened.Show();
            return opened;
        }
        void ConfigureEndpoint(string endpoint)
        {
            Control<TextBox>("ModelEndpoint").Text = endpoint;
            Click(Control<Button>("SaveSettingsButton"));
        }
        Task SendInitiative(InitiativeTrigger trigger) =>
            (Task)(typeof(MainWindow).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [false, trigger]) ?? throw new InvalidOperationException("Missing proactive task"));
    }

    private static async Task VerifyChatBackgroundAsync(string root)
    {
        var store = new PortableStore(root);
        store.Initialize();
        var state = new AppState();
        var first = state.Friends[0];
        first.Messages = [new() { Role = "user", Text = "今天想换一个聊天背景。" },
            new() { Role = "assistant", Text = "选好了就换上。聊天记录还在，放心。" }];
        first.Memories = [new() { Content = "背景功能测试记忆", State = "approved" }];
        state.Friends.Add(new FriendProfile { Id = "background-second", Name = "第二位朋友" });
        store.Save(state);
        string source = Path.Combine(root, "source-background.png");
        string tallSource = Path.Combine(root, "source-tall.png");
        CreateBackgroundFixture(source, 900, 600);
        CreateBackgroundFixture(tallSource, 64, 4096);
        MainWindow? window = null;
        try
        {
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(Control<Border>("ChatBackgroundLayer").Background == null
                && Control<Image>("BackgroundPreview").Visibility == Visibility.Collapsed
                && !Control<Button>("RemoveBackgroundButton").IsEnabled,
                "a character without a background retains the default chat surface and no preview");
            Apply(source);
            string firstPath = state.Friends[0].ChatBackgroundPath;
            string firstFile = store.ResolveAsset(firstPath)!;
            Check(firstPath.Length > 0 && File.Exists(firstFile) && firstFile != source
                && File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(firstFile)),
                "background selection copies the image into portable managed assets");
            Check(Control<Border>("ChatBackgroundLayer").Background is ImageBrush { ImageSource: not null, Stretch: Stretch.UniformToFill }
                && Control<Image>("BackgroundPreview").Source != null
                && Control<Image>("BackgroundPreview").Visibility == Visibility.Visible
                && Control<Button>("RemoveBackgroundButton").IsEnabled,
                "selected background renders behind chat with a settings preview and removal action");
            File.Delete(source);
            Control<ListBox>("FriendList").SelectedItem = state.Friends[1];
            Check(Control<Border>("ChatBackgroundLayer").Background == null
                && Control<Image>("BackgroundPreview").Source == null,
                "switching to an unconfigured character does not leak the previous background");
            Apply(tallSource);
            string secondPath = state.Friends[1].ChatBackgroundPath;
            var decoded = ((ImageBrush)Control<Border>("ChatBackgroundLayer").Background).ImageSource as BitmapSource;
            Check(decoded is { PixelWidth: > 0 and <= 1920, PixelHeight: > 0 and <= 1920 }
                && state.Friends[0].ChatBackgroundPath == firstPath && secondPath != firstPath,
                "a tall background decodes within the bounded image size and remains independent per character");
            Control<ListBox>("FriendList").SelectedItem = state.Friends[0];
            Check(Control<Border>("ChatBackgroundLayer").Background is ImageBrush { ImageSource: not null }
                && state.Friends[0].ChatBackgroundPath == firstPath,
                "the original background reloads after its source file is deleted");
            string invalid = Path.Combine(root, "invalid.png");
            File.WriteAllText(invalid, "not a PNG image");
            string[] assetsBefore = Directory.GetFiles(store.AssetsDirectory).Order().ToArray();
            bool rejected = false;
            try { Apply(invalid); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { rejected = true; }
            Check(rejected && state.Friends[0].ChatBackgroundPath == firstPath
                && Control<Border>("ChatBackgroundLayer").Background is ImageBrush { ImageSource: not null }
                && Directory.GetFiles(store.AssetsDirectory).Order().SequenceEqual(assetsBefore),
                "invalid image import preserves the chosen background and cleans its rejected asset");
            Click(Control<Button>("ChatNavButton"));
            window.Width = 940;
            window.Height = 680;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Check(Control<Border>("ChatBackgroundLayer").ActualWidth > 200
                && Control<Border>("ChatBackgroundLayer").ActualHeight > 100
                && Control<TextBox>("InputBox").ActualWidth > 200
                && Control<StackPanel>("MessagesPanel").Children.Count == 2,
                "background and chat content remain usable together at the minimum window size");
            Capture(window, Path.Combine(root, "chat-background.png"));
            window.Close();
            window = null;
            store = new PortableStore(root);
            store.Initialize();
            state = store.Load();
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(state.Friends[0].ChatBackgroundPath == firstPath && state.Friends[1].ChatBackgroundPath == secondPath
                && Control<Border>("ChatBackgroundLayer").Background is ImageBrush { ImageSource: not null },
                "each character's background survives closing and reopening the application");
            Click(Control<Button>("SettingsNavButton"));
            Click(Control<Button>("RemoveBackgroundButton"));
            Check(state.Friends[0].ChatBackgroundPath.Length == 0
                && Control<Border>("ChatBackgroundLayer").Background == null
                && Control<Image>("BackgroundPreview").Visibility == Visibility.Collapsed
                && !Control<Button>("RemoveBackgroundButton").IsEnabled,
                "remove background restores the default surface and clears the preview");
            Check(!File.Exists(firstFile) && File.Exists(store.ResolveAsset(secondPath)),
                "removing a background reclaims only its orphan asset while preserving another character's image");
            var saved = store.Load();
            Check(saved.Friends[0].ChatBackgroundPath.Length == 0 && saved.Friends[1].ChatBackgroundPath == secondPath
                && saved.Friends[0].Messages.Count == 2 && saved.Friends[0].Memories.Single().Content == "背景功能测试记忆",
                "background removal persists without altering chat history, long-term memory or other backgrounds");
        }
        finally { window?.Close(); }

        T Control<T>(string name) where T : FrameworkElement => window?.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing background control: {name}");
        MainWindow Open()
        {
            var opened = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowActivated = false, ShowInTaskbar = false
            };
            opened.Show();
            return opened;
        }
        void Apply(string file) => typeof(MainWindow).GetMethod("ApplyImage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [file, "background"]);
    }

    private static void CreateBackgroundFixture(string path, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(147, 195, 210), Color.FromRgb(247, 211, 172), 90),
                null, new Rect(0, 0, width, height));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 239, 191)), null,
                new Point(width * .76, height * .28), width * .10, width * .10);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(103, 150, 143)), null,
                new Point(width * .18, height * 1.07), width * .75, height * .47);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(67, 110, 103)), null,
                new Point(width * .95, height * 1.08), width * .65, height * .40);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    private static Button FindButton(DependencyObject root, string label) => Descendants(root).OfType<Button>().FirstOrDefault(b =>
        b.Content is string text && text == label || Descendants(b).OfType<TextBlock>().Any(t => t.Text == label))
        ?? throw new InvalidOperationException("Button not found: " + label);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var nested in Descendants(child)) yield return nested;
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        passed++;
        Console.WriteLine("PASS " + description);
    }
    private static async Task UntilAsync(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(label);
            await Task.Delay(20);
        }
    }
    private static string FindWorkspace()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "Qiban.Desktop", "Qiban.Desktop.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("Run the integration project from the Qiban workspace.");
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private sealed record RecordedRequest(string Path, string Body);
    private sealed record RecordedVoiceRequest(string Path, string Body, string Authorization);
    private sealed class LocalVoiceServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        private readonly TaskCompletionSource<RecordedVoiceRequest> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource audioStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task completion;
        public string BaseAddress { get; }
        public Task<RecordedVoiceRequest> Request => request.Task;
        public Task AudioStarted => audioStarted.Task;

        public LocalVoiceServer()
        {
            listener.Start();
            BaseAddress = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            completion = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, cts.Token) == 0) throw new IOException("Voice fixture request ended early.");
                    header.Add(one[0]);
                    int count = header.Count;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10
                        && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                if (!lines[0].StartsWith("POST ", StringComparison.Ordinal)) throw new InvalidDataException("Voice fixture expects synthesis POST.");
                int size = int.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1].Trim(), CultureInfo.InvariantCulture);
                if (size is < 0 or > 1_000_000) throw new InvalidDataException("Voice fixture body size invalid.");
                byte[] bytes = new byte[size];
                await stream.ReadExactlyAsync(bytes, cts.Token);
                string auth = lines.FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim() ?? "";
                request.TrySetResult(new(lines[0].Split(' ')[1], Encoding.UTF8.GetString(bytes), auth));

                // A real PCM WAV begins downloading, but never completes. Tests cancel/switch roles
                // before synthesis can hand an audio file to MediaPlayer, so this fixture is silent.
                byte[] wave = new byte[48];
                "RIFF"u8.CopyTo(wave); BitConverter.GetBytes(32036).CopyTo(wave, 4); "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
                BitConverter.GetBytes(16).CopyTo(wave, 16); BitConverter.GetBytes((short)1).CopyTo(wave, 20);
                BitConverter.GetBytes((short)1).CopyTo(wave, 22); BitConverter.GetBytes(16000).CopyTo(wave, 24);
                BitConverter.GetBytes(32000).CopyTo(wave, 28); BitConverter.GetBytes((short)2).CopyTo(wave, 32);
                BitConverter.GetBytes((short)16).CopyTo(wave, 34); "data"u8.CopyTo(wave.AsSpan(36));
                BitConverter.GetBytes(32000).CopyTo(wave, 40);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nContent-Length: 32044\r\nConnection: close\r\n\r\n"), cts.Token);
                await stream.WriteAsync(wave, cts.Token);
                await stream.FlushAsync(cts.Token);
                audioStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                request.TrySetCanceled(); audioStarted.TrySetCanceled();
            }
            catch (Exception error)
            {
                request.TrySetException(error); audioStarted.TrySetException(error); throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel(); listener.Stop();
            try { await completion; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    private sealed class LocalChatServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        private readonly TaskCompletionSource<RecordedRequest> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Endpoint { get; }
        public Task<RecordedRequest> Request => request.Task;
        public Task Completion { get; }
        public LocalChatServer(bool holdAfterFirst, bool holdBeforeFirst = false)
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            Completion = Task.Run(() => ServeAsync(holdAfterFirst, holdBeforeFirst));
        }
        private async Task ServeAsync(bool hold, bool holdBeforeFirst)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, cts.Token) == 0) throw new IOException("Request header ended early.");
                    header.Add(one[0]);
                    int n = header.Count;
                    if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                string path = lines[0].Split(' ')[1];
                string sizeHeader = lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                int size = int.Parse(sizeHeader.Split(':', 2)[1].Trim(), CultureInfo.InvariantCulture);
                if (size is < 0 or > 1_000_000) throw new InvalidDataException("Unexpected fixture body size.");
                byte[] bytes = new byte[size];
                await stream.ReadExactlyAsync(bytes, cts.Token);
                request.TrySetResult(new RecordedRequest(path, Encoding.UTF8.GetString(bytes)));
                if (holdBeforeFirst) { await Task.Delay(Timeout.Infinite, cts.Token); return; }
                await Write("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                await Write("data: {\"choices\":[{\"delta\":{\"content\":\"记得呀，\"},\"finish_reason\":null}]}\n\n");
                if (hold) { await Task.Delay(Timeout.Infinite, cts.Token); return; }
                await Task.Delay(150, cts.Token);
                await Write("data: {\"choices\":[{\"delta\":{\"content\":\"你喜欢喝乌龙茶。\"},\"finish_reason\":null}]}\n\n");
                await Write("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                async Task Write(string text) { await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cts.Token); await stream.FlushAsync(cts.Token); }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { request.TrySetCanceled(); }
            catch (Exception e) { request.TrySetException(e); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            try { await Completion; }
            catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }
}
