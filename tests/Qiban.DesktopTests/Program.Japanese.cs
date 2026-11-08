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
    private static async Task VerifyJapaneseChatAsync(string root)
    {
        var store = new PortableStore(root);
        store.Initialize();
        var state = new AppState();
        state.Friends[0].Name = "望舒";
        state.Friends[0].Persona = "你是温和、喜欢星空的朋友。";
        state.Friends.Add(new FriendProfile { Id = "japanese-second", Name = "青禾" });
        state.Settings.Model = "japanese-fixture";
        store.Save(state);
        MainWindow? window = null;
        try
        {
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            var friend = state.Friends[0];
            var second = state.Friends[1];
            Control<CheckBox>("JapaneseMode").IsChecked = true;
            Language("Français");
            Save();
            Check(friend.JapaneseChat.Enabled && friend.JapaneseChat.DisplayLanguage == "Français"
                && !second.JapaneseChat.Enabled && friend.Voice.Language == "zh",
                "Japanese mode saves a custom display language per character without changing the reference-audio language");
            Control<ListBox>("FriendList").SelectedItem = second;
            Check(Control<CheckBox>("JapaneseMode").IsChecked == false,
                "switching to another character loads that character's independent Japanese mode");
            Control<ListBox>("FriendList").SelectedItem = friend;
            Check(Control<CheckBox>("JapaneseMode").IsChecked == true
                && Control<ComboBox>("ChatDisplayLanguage").Text == "Français",
                "switching back restores the selected custom display language in the editable control");
            window.Close(); window = null;
            store = new PortableStore(root); store.Initialize(); state = store.Load();
            friend = state.Friends[0]; second = state.Friends[1];
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Click(Control<Button>("SettingsNavButton"));
            Check(friend.JapaneseChat.Enabled && friend.JapaneseChat.DisplayLanguage == "Français"
                && Control<ComboBox>("ChatDisplayLanguage").Text == "Français" && !second.JapaneseChat.Enabled,
                "Japanese mode and arbitrary display-language selection survive a complete window and store restart");

            const string japanese = "今夜の星はきれいですね。少し一緒に眺めませんか。";
            const string chinese = "今晚的星星很美，要一起看一会儿吗？";
            await using (var model = new JapaneseModelServer(japanese, chinese) { PauseBeforeResponse = 1 })
            await using (var voice = new LocalVoiceServer())
            {
                Configure(model, "简体中文", voice);
                Click(Control<Button>("ChatNavButton"));
                Control<TextBox>("InputBox").Text = "How are the stars tonight?";
                Click(Control<Button>("SendButton"));
                await UntilAsync(() => model.Requests.Count == 2, "Japanese original followed by display translation");
                Check(friend.Messages[^2].Text == "How are the stars tonight?"
                    && !Descendants(Control<StackPanel>("MessagesPanel")).OfType<TextBlock>().Any(t => t.Text.Contains("今夜の星")),
                    "foreign-language user input remains unchanged and untranslated Japanese never flashes into translated chat bubbles");
                model.ReleaseResponse.TrySetResult();
                var request = await voice.Request.WaitAsync(TimeSpan.FromSeconds(10));
                using var payload = JsonDocument.Parse(request.Body);
                var answer = friend.Messages.Last();
                Check(answer.Text == chinese && answer.JapaneseText == japanese && answer.DisplayLanguage == "简体中文"
                    && answer.Status == "complete" && payload.RootElement.GetProperty("text").GetString() == japanese
                    && payload.RootElement.GetProperty("text_lang").GetString() == "ja"
                    && payload.RootElement.GetProperty("prompt_lang").GetString() == "zh",
                    "translated text and Japanese original are separate, while automatic speech is Japanese with the configured Chinese reference language");
                var committed = store.Load().Friends[0].Messages.Last();
                Check(committed.Text == chinese && committed.JapaneseText == japanese && committed.DisplayLanguage == "简体中文",
                    "both the displayed translation and original Japanese persist before automatic speech starts");
                Capture(window, Path.Combine(root, "japanese-chat.png"));
                await voice.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Click(Control<Button>("SettingsNavButton"));
                Control<CheckBox>("JapaneseMode").IsChecked = false;
                Save();
                await UntilAsync(() => Private("_voiceCts") == null, "Japanese speech cancelled after mode change");
                Check(Private("_playingFile") == null
                    && !Directory.EnumerateFiles(store.CacheDirectory, "voice-*", SearchOption.AllDirectories).Any(),
                    "changing Japanese mode cancels in-flight audio and removes partial files without playback");
            }

            await using (var model = new JapaneseModelServer("ええ、今日はゆっくり話しましょう。"))
            {
                Configure(model, "日本語");
                Click(Control<Button>("ChatNavButton"));
                Control<TextBox>("InputBox").Text = "¿Podemos hablar un momento?";
                await Send();
                var answer = friend.Messages.Last();
                Check(model.Requests.Count == 1 && answer.Status == "complete" && answer.Text == answer.JapaneseText
                    && answer.Text == "ええ、今日はゆっくり話しましょう。" && friend.Messages[^2].Text == "¿Podemos hablar un momento?",
                    "Japanese display skips translation while accepting another input language unchanged");
            }

            await using (var model = new JapaneseModelServer("少し休みませんか。お茶でもいれましょう。", "Shall we take a short break and make some tea?"))
            {
                Configure(model, "English");
                int users = friend.Messages.Count(m => m.Role == "user");
                await Send(new InitiativeTrigger("activity", new ActivityObservation("fixture-browser", "浏览器"), 0));
                var answer = friend.Messages.Last();
                Check(answer.Trigger == "activity" && answer.Status == "complete"
                    && answer.Text == "Shall we take a short break and make some tea?"
                    && answer.JapaneseText == "少し休みませんか。お茶でもいれましょう。"
                    && friend.Messages.Count(m => m.Role == "user") == users && model.Requests.Count == 2,
                    "proactive conversation follows the same Japanese-original and chosen-display-language pipeline without a fabricated user turn");
            }

            const string preserved = "もちろんです。あなたの話を聞かせてください。";
            await using (var model = new JapaneseModelServer(preserved, null, "当然可以，请告诉我吧。"))
            await using (var voice = new LocalVoiceServer())
            {
                Configure(model, "简体中文", voice);
                Click(Control<Button>("ChatNavButton"));
                Control<TextBox>("InputBox").Text = "Can I tell you something?";
                await Send();
                var failed = friend.Messages.Last();
                Check(failed.Status == "failed" && failed.JapaneseText == preserved && !voice.Request.IsCompleted
                    && store.Load().Friends[0].Messages.Last().JapaneseText == preserved,
                    "translation failure preserves the completed Japanese original and never falls back to non-Japanese speech");
                await Send(retry: true);
                var request = await voice.Request.WaitAsync(TimeSpan.FromSeconds(10));
                using var payload = JsonDocument.Parse(request.Body);
                Check(model.Requests.Count == 3 && friend.Messages.Last().Status == "complete"
                    && friend.Messages.Last().JapaneseText == preserved && friend.Messages.Last().Text == "当然可以，请告诉我吧。"
                    && payload.RootElement.GetProperty("text").GetString() == preserved,
                    "retry translates the retained Japanese original instead of generating a different reply or reading the translation");
                await voice.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Click(Control<Button>("StopButton"));
                await UntilAsync(() => Private("_voiceCts") == null, "stopping Japanese retry speech");
            }

            await using (var model = new JapaneseModelServer("This answer ignored the requested language.", "Still not Japanese."))
            await using (var voice = new LocalVoiceServer())
            {
                Configure(model, "English", voice);
                Control<TextBox>("InputBox").Text = "Please continue.";
                await Send();
                Check(friend.Messages.Last().Status == "failed" && friend.Messages.Last().JapaneseText == ""
                    && !voice.Request.IsCompleted && model.Requests.Count == 2,
                    "non-Japanese output fails after one bounded correction without display translation or speech");
            }

            const string legacyText = "Let's sit together and listen to the rain.";
            await using (var model = new JapaneseModelServer("一緒に座って、雨の音を聞きましょう。"))
            await using (var voice = new LocalVoiceServer())
            {
                Configure(model, "English", voice);
                var legacy = new ChatMessage { Role = "assistant", Text = legacyText };
                friend.Messages.Add(legacy); store.Save(state);
                Invoke("RenderMessages");
                var bubble = Descendants(Control<StackPanel>("MessagesPanel")).OfType<Border>()
                    .Single(b => b.Child is TextBlock t && t.Text == legacyText);
                var menu = bubble.ContextMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "朗读消息"));
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
                var request = await voice.Request.WaitAsync(TimeSpan.FromSeconds(10));
                using var payload = JsonDocument.Parse(request.Body);
                Check(model.Requests.Count == 1 && legacy.Text == legacyText
                    && payload.RootElement.GetProperty("text").GetString() == "一緒に座って、雨の音を聞きましょう。"
                    && payload.RootElement.GetProperty("text_lang").GetString() == "ja",
                    "manual reading of an older foreign-language message translates it into Japanese without replacing its displayed history");
                await voice.AudioStarted.WaitAsync(TimeSpan.FromSeconds(10));
                Click(Control<Button>("StopButton"));
                await UntilAsync(() => Private("_voiceCts") == null, "stopping translated legacy speech");
            }

            Click(Control<Button>("SettingsNavButton"));
            Descendants(window).OfType<TabItem>().Single(t => Equals(t.Header, "角色声音")).IsSelected = true;
            window.Width = 940; window.Height = 680;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture(window, Path.Combine(root, "japanese-settings.png"));
            window.Close(); window = null;
            store = new PortableStore(root); store.Initialize(); state = store.Load();
            friend = state.Friends[0];
            window = Open();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(friend.Messages.Any(m => m.Text == chinese && m.JapaneseText == japanese)
                && friend.Messages.Any(m => m.Text == "当然可以，请告诉我吧。" && m.JapaneseText == preserved)
                && friend.Messages.Any(m => m.Text == legacyText) && friend.Messages.Any(m => m.Text == "How are the stars tonight?"),
                "Japanese originals, translations and untouched historical/user text survive final application restart together");
        }
        finally { window?.Close(); }
        await VerifyJapaneseCorrectionAsync(Path.Combine(root, "correction"));

        T Control<T>(string name) where T : FrameworkElement => window?.FindName(name) as T
            ?? throw new InvalidOperationException($"Missing Japanese chat control: {name}");
        object? Private(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        void Invoke(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        void Save() => Click(Control<Button>("SaveSettingsButton"));
        void Language(string value)
        {
            var control = Control<ComboBox>("ChatDisplayLanguage");
            control.SelectedIndex = -1;
            control.Text = value;
        }
        void Configure(JapaneseModelServer model, string language, LocalVoiceServer? voice = null)
        {
            var friend = state.Friends[0];
            state.Settings.Endpoint = model.Endpoint;
            friend.JapaneseChat.Enabled = true;
            friend.JapaneseChat.DisplayLanguage = language;
            friend.Voice = new VoiceSettings { Enabled = voice != null, AutoPlay = voice != null,
                Provider = "GPT-SoVITS", Endpoint = voice?.BaseAddress + "/tts", Language = "zh",
                ReferenceAudio = "D:/fixture/chinese-reference.wav", ReferenceText = "中文参考音频文本" };
            Invoke("LoadSettings"); store.Save(state);
        }
        Task Send(InitiativeTrigger? trigger = null, bool retry = false) => (Task)typeof(MainWindow)
            .GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [retry, trigger])!;
        MainWindow Open()
        {
            var opened = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
            { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            opened.Show(); return opened;
        }
    }

    /// <summary>Local Chat Completions fixture with ordered replies, failures and a translation gate.</summary>
    private sealed class JapaneseModelServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new(TimeSpan.FromSeconds(40));
        private readonly Task completion;
        public ConcurrentQueue<RecordedRequest> Requests { get; } = new();
        public TaskCompletionSource ReleaseResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PauseBeforeResponse { get; init; } = -1;
        public string Endpoint { get; }

        public JapaneseModelServer(params string?[] responses)
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            completion = Task.Run(async () =>
            {
                int index = 0;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(cts.Token);
                        await using var stream = client.GetStream();
                        var header = new List<byte>(); var one = new byte[1];
                        while (header.Count < 16384)
                        {
                            if (await stream.ReadAsync(one, cts.Token) == 0) throw new IOException("Japanese fixture header ended early.");
                            header.Add(one[0]); int count = header.Count;
                            if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                        }
                        string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                        int size = int.Parse(lines.Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim(), CultureInfo.InvariantCulture);
                        if (size is < 0 or > 1_000_000) throw new InvalidDataException("Japanese fixture request size invalid.");
                        byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, cts.Token);
                        Requests.Enqueue(new RecordedRequest(lines[0].Split(' ')[1], Encoding.UTF8.GetString(bytes)));
                        if (index == PauseBeforeResponse) await ReleaseResponse.Task.WaitAsync(cts.Token);
                        string? reply = index < responses.Length ? responses[index] : null;
                        index++;
                        string body = reply == null ? "{\"error\":\"fixture translation failure\"}"
                            : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = reply } } } });
                        byte[] content = Encoding.UTF8.GetBytes(body);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(reply == null ? "503 Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), cts.Token);
                        await stream.WriteAsync(content, cts.Token); await stream.FlushAsync(cts.Token);
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel(); listener.Stop();
            try { await completion; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }
}
