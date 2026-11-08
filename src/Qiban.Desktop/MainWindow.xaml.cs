using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Qiban.Core;

namespace Qiban.Desktop;

public partial class MainWindow : Window
{
    private readonly PortableStore _store;
    private readonly AppState _state;
    private readonly ChatClient _chat = new();
    private readonly VoiceClient _voice;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _janitor = new() { Interval = TimeSpan.FromMinutes(3) };
    private CancellationTokenSource? _chatCts;
    private CancellationTokenSource? _voiceCts;
    private bool _ready;
    private bool _busy;
    private bool _refreshingFriends;
    private bool _closing;
    private bool _cleanCacheAfterVoice;
    private DateTimeOffset _lastVoiceActivity;
    private string? _playingFile;
    private FriendProfile? _voiceSelection;
    private long _voiceEpoch;
    private MemoryItem? _selectedMemory;
    private FriendProfile Friend => _state.Friends.First(x => x.Id == _state.SelectedFriendId);

    public MainWindow(PortableStore store, AppState state, bool enableInitiative = true, InitiativeEnvironment? initiativeEnvironment = null, bool enableMemoryLearning = true)
    {
        _store = store;
        _state = state;
        _voice = new VoiceClient(store.Root);
        _initiativeEnvironment = initiativeEnvironment ?? new();
        _initiativeScheduler = new(_initiativeEnvironment.Random);
        InitializeComponent();
        InitializeWindowChrome();
        if (_state.Friends.Count == 0) _state.Friends.Add(new());
        if (!_state.Friends.Any(x => x.Id == _state.SelectedFriendId)) _state.SelectedFriendId = _state.Friends[0].Id;
        _ready = true;
        RefreshFriends();
        LoadSettings();
        RenderFriend();
        _janitor.Tick += (_, _) =>
        {
            if (_voiceCts != null || _playingFile != null) return;
            if (_voice.IsEmbeddedRunning && DateTimeOffset.UtcNow - _lastVoiceActivity >= TimeSpan.FromMinutes(3)) _voice.StopEmbedded();
            if (!_voice.IsEmbeddedRunning) TryClean(false);
            UpdateVoiceProviderUi();
        };
        _janitor.Start();
        _player.MediaEnded += (_, _) => StopPlayback();
        _player.MediaFailed += (_, _) => { StopPlayback(); Notice("音频无法播放，请确认服务返回 WAV 或 MP3。"); };
        Closing += OnClosing;
        InitializeInitiative(enableInitiative);
        InitializeMemoryLearning(enableMemoryLearning);
        Notice(_store.RecoveryNotice ?? "");
    }

    private void Notice(string text) => NoticeText.Text = _store.PersistenceWarning is { } warning
        ? (string.IsNullOrEmpty(text) ? warning : text + " · " + warning) : text;
    private void Guard(Action action) { try { action(); } catch (Exception e) { Notice(Boot.SafeError(e)); } }
    private void Save(bool purgeBackup = false)
    {
        try { _store.Save(_state, purgeBackup); }
        catch
        {
            var draft = (Id: _selectedMemory?.Id, Text: MemoryContent.Text,
                Category: Combo(MemoryCategory), Pinned: MemoryPinned.IsChecked, Expiry: MemoryExpiry.Text);
            try
            {
                var committed = _store.Load();
                _state.Settings = committed.Settings; _state.Friends = committed.Friends; _state.SelectedFriendId = committed.SelectedFriendId;
                _state.StorageRevision = committed.StorageRevision;
                _state.StorageToken = committed.StorageToken;
                _selectedMemory = null;
                if (!_closing) Dispatcher.BeginInvoke(() =>
                {
                    RefreshFriends(); LoadSettings(); RenderFriend(); RefreshMemories();
                    // Retain the user's editable draft after rolling back uncommitted model objects.
                    _selectedMemory = Friend.Memories.FirstOrDefault(m => m.Id == draft.Id);
                    MemoryContent.Text = draft.Text; SetCombo(MemoryCategory, draft.Category);
                    MemoryPinned.IsChecked = draft.Pinned; MemoryExpiry.Text = draft.Expiry;
                });
            }
            catch { /* Preserve in-memory data if both read and write are unavailable. */ }
            throw;
        }
        try { _store.PruneAssets(_state); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static string Combo(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? box.Text;
    private static void SetCombo(ComboBox box, string value)
    {
        box.SelectedItem = box.Items.Cast<object>().FirstOrDefault(x => (x as ComboBoxItem)?.Content?.ToString() == value);
    }
    private void RefreshFriends()
    {
        _refreshingFriends = true;
        try
        {
            FriendList.ItemsSource = null;
            FriendList.ItemsSource = _state.Friends;
            FriendList.SelectedItem = Friend;
        }
        finally { _refreshingFriends = false; }
    }
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        var page = (sender as FrameworkElement)?.Tag?.ToString() ?? "chat";
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        MemoryPage.Visibility = page == "memory" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var nav in new[] { ChatNavButton, MemoryNavButton, SettingsNavButton })
        {
            if (nav.Tag?.ToString() == page) nav.SetResourceReference(BackgroundProperty, "NavSelectedBrush");
            else nav.Background = Brushes.Transparent;
        }
        if (page == "memory") RefreshMemories();
        if (page == "settings") { LoadSettings(); RefreshStorage(); }
    }
    private void OnFriendSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshingFriends || FriendList.SelectedItem is not FriendProfile profile || profile.Id == _state.SelectedFriendId) return;
        _state.SelectedFriendId = profile.Id;
        RenderFriend();
        LoadSettings();
        RefreshMemories();
        Guard(() => Save());
    }
    private void RenderFriend()
    {
        CancelMemoryLearning(preserveFailure: true);
        SyncVoiceSelection();
        FriendName.Text = Friend.Name;
        FriendTagline.Text = Friend.Tagline;
        AvatarFallback.Text = Friend.Name.Length > 0 ? Friend.Name[..1] : "友";
        AvatarImage.Source = LoadImage(Friend.AvatarPath);
        AvatarFallback.Visibility = AvatarImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        PortraitImage.Source = LoadImage(Friend.PortraitPath);
        PortraitPanel.Visibility = PortraitImage.Source == null ? Visibility.Collapsed : Visibility.Visible;
        var background = LoadImage(Friend.ChatBackgroundPath);
        ChatBackgroundLayer.Background = background == null ? null : new ImageBrush(background)
        {
            Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center, Opacity = 0.55
        };
        BackgroundPreview.Source = background;
        BackgroundPreview.Visibility = background == null ? Visibility.Collapsed : Visibility.Visible;
        RemoveBackgroundButton.IsEnabled = !string.IsNullOrEmpty(Friend.ChatBackgroundPath);
        StatusText.Text = string.IsNullOrWhiteSpace(_state.Settings.Model) ? "模型未配置" : _state.Settings.Model;
        RenderMessages();
        ResetInitiative();
        UpdateMemoryLearningUi();
    }
    private ImageSource? LoadImage(string path)
    {
        try
        {
            var resolved = _store.ResolveAsset(path);
            if (resolved == null || !File.Exists(resolved)) return null;
            using var stream = File.OpenRead(resolved);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            // Bound both landscape and portrait images by their longest edge.
            if (width >= height) bitmap.DecodePixelWidth = Math.Min(width, 1920);
            else bitmap.DecodePixelHeight = Math.Min(height, 1920);
            bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }
    private void RenderMessages()
    {
        MessagesPanel.Children.Clear();
        if (Friend.Messages.Count == 0)
        {
            AddBubble(new ChatMessage { Role = "assistant", Text = Friend.Greeting }, false);
            return;
        }
        if (Friend.Messages.Count > 200)
            MessagesPanel.Children.Add(new TextBlock { Text = "显示最近 200 条消息 · 完整记录可在角色设置中导出", Foreground = Brushes.SlateGray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) });
        foreach (var message in Friend.Messages.TakeLast(200)) AddBubble(message);
        ChatScroll.ScrollToEnd();
    }
    private TextBlock AddBubble(ChatMessage message, bool actions = true)
    {
        bool mine = message.Role == "user";
        Brush captionBackground = ChatBackgroundLayer.Background == null ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        var stack = new StackPanel { Margin = new Thickness(mine ? 60 : 0, 5, mine ? 0 : 60, 10), HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, MaxWidth = 620 };
        var caption = new TextBlock { Text = mine ? "你" : Friend.Name, FontSize = 11, Background = captionBackground, Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(4, 0, 4, 5), HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        caption.SetResourceReference(TextBlock.ForegroundProperty, ChatBackgroundLayer.Background == null ? "ChatCaptionBrush" : "FieldTextBrush");
        stack.Children.Add(caption);
        var text = new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "BubbleTextBrush");
        var bubble = new Border { Padding = new Thickness(17, 11, 17, 12), Child = text };
        bubble.SetResourceReference(Border.BackgroundProperty, mine ? "UserBubbleBrush" : "AssistantBubbleBrush");
        bubble.SetResourceReference(Border.CornerRadiusProperty, "ChatBubbleCornerRadius");
        bubble.SetResourceReference(Border.BorderBrushProperty, "BubbleBorderBrush");
        bubble.SetResourceReference(Border.BorderThicknessProperty, "BubbleBorderThickness");
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制消息" }; copy.Click += (_, _) => Guard(() => Clipboard.SetText(message.Text)); menu.Items.Add(copy);
        if (actions && mine)
        {
            var remember = new MenuItem { Header = "加入待确认记忆" };
            remember.Click += (_, _) => Guard(() =>
            {
                if (Friend.Memories.Any(x => x.Content == message.Text && x.State != "archived")) { Notice("记忆已存在"); return; }
                Friend.Memories.Add(new MemoryItem { Content = message.Text, SourceMessageId = message.Id, Category = "事实" }); Save(); Notice("已加入待确认记忆");
            });
            menu.Items.Add(remember);
        }
        if (actions && !mine && message.Status == "complete")
        {
            var owner = Friend;
            var speak = new MenuItem { Header = "朗读消息" }; speak.Click += async (_, _) => await SpeakMessageAsync(owner, message); menu.Items.Add(speak);
        }
        bubble.ContextMenu = menu;
        stack.Children.Add(bubble);
        var note = message.Status switch { "interrupted" => " · 已中止", "failed" => " · 未完成", "streaming" => " · 正在输入", _ => "" };
        var timestamp = new TextBlock { Text = message.CreatedAt.ToLocalTime().ToString("HH:mm") + note, FontSize = 10, Background = captionBackground, Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(4, 5, 4, 0), HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        timestamp.SetResourceReference(TextBlock.ForegroundProperty, ChatBackgroundLayer.Background == null ? "ChatTimeBrush" : "FieldTextBrush");
        stack.Children.Add(timestamp);
        MessagesPanel.Children.Add(stack);
        return text;
    }
    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync(false);
    private async void OnRetry(object sender, RoutedEventArgs e) => await SendAsync(true);
    private async void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Intercept Return before the multiline TextBox inserts a newline. IME-processed
        // keys are deliberately left to the input method, as is Shift+Return.
        if (e.Key != Key.Enter || e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (!e.IsRepeat) await SendAsync(false);
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy || _initiativeSending;
        UpdateStopButton();
        FriendList.IsEnabled = !busy;
        SettingsPage.IsEnabled = !busy;
        MemoryPage.IsEnabled = !busy;
        UpdateMemoryLearningUi();
    }
    private async Task SendAsync(bool retry, InitiativeTrigger? initiative = null)
    {
        if (_busy && _initiativeSending && initiative == null && !retry && InputBox.Text.Trim().Length > 0)
        {
            _chatCts?.Cancel();
            if (_initiativeTask != null) await _initiativeTask;
        }
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_state.Settings.Model)) { Notice("模型未配置：设置 → 对话模型"); return; }
        CancelMemoryLearning();
        var friend = Friend;
        ChatMessage? answer = null;
        ChatMessage? translationRetry = null;
        bool completed = false;
        try
        {
            if (initiative != null)
            {
                _initiativeSending = true;
            }
            else if (retry)
            {
                var last = friend.Messages.LastOrDefault();
                if (last?.Role == "assistant" && last.Status != "complete")
                {
                    if (friend.JapaneseChat.Enabled && !string.IsNullOrWhiteSpace(last.JapaneseText))
                        try { JapaneseChat.EnsureJapanese(last.JapaneseText); translationRetry = last; } catch (InvalidDataException) { }
                    if (translationRetry == null) friend.Messages.Remove(last);
                }
                if (translationRetry == null && friend.Messages.LastOrDefault()?.Role != "user") { Notice("无待重试消息"); return; }
            }
            else
            {
                var input = InputBox.Text.Trim();
                if (input.Length == 0) return;
                if (input.Length > 16000) { Notice("单条消息请控制在 16000 字以内。"); return; }
                var user = new ChatMessage { Text = input };
                friend.Messages.Add(user);
                if (_state.Settings.SuggestMemories && (!_state.Settings.AutoMemoryEnabled || !_memoryLearningEnabled))
                    MemoryEngine.SuggestFromMessage(friend, user);
                Save(); InputBox.Clear();
            }
            _profileCts?.Cancel(); // Conversation takes priority over auxiliary cadence analysis.
            if (initiative == null)
            {
                _initiativeScheduler.UserMessage(_initiativeEnvironment.Now());
            }
            SetBusy(true);
            _chatCts = new CancellationTokenSource();
            var query = friend.Messages.LastOrDefault(x => x.Role == "user")?.Text ?? "";
            if (initiative?.Activity is { } activity) query += " " + activity.ProcessName + " " + activity.Category;
            var memories = MemoryEngine.Retrieve(friend, query, _state.Settings.MemoryBudget);
            RenderMessages();
            answer = translationRetry ?? new ChatMessage { Role = "assistant", Text = "", Trigger = initiative?.Kind ?? "" };
            answer.Status = "streaming";
            if (translationRetry == null) friend.Messages.Add(answer);
            else { answer.Text = ""; RenderMessages(); }
            var textBlock = translationRetry == null ? AddBubble(answer) : null;
            Notice(initiative != null ? "正在输入" : memories.Count > 0 ? $"正在回复 · 参考了 {memories.Count} 条已确认记忆" : "正在回复…");
            await CompleteReplyAsync(friend, answer, memories, initiative, textBlock, translationRetry != null, _chatCts.Token);
            if (string.IsNullOrWhiteSpace(answer.Text)) throw new InvalidOperationException("模型返回了空消息，请检查模型是否支持聊天接口。");
            answer.Status = "complete";
            Save();
            completed = true;
            Notice("");
            if (friend.Voice.Enabled && friend.Voice.AutoPlay) _ = SpeakMessageAsync(friend, answer);
        }
        catch (Exception e)
        {
            if (initiative != null && e is not OperationCanceledException) _initiativeError = Boot.SafeError(e);
            if (answer != null) answer.Status = e is OperationCanceledException ? "interrupted" : "failed";
            if (initiative != null && answer != null && string.IsNullOrWhiteSpace(answer.Text) && string.IsNullOrWhiteSpace(answer.JapaneseText)) friend.Messages.Remove(answer);
            if (!_closing)
            {
                Guard(() => Save());
                if (initiative == null || e is not OperationCanceledException) Notice(Boot.SafeError(e));
            }
        }
        finally
        {
            _chatCts?.Dispose(); _chatCts = null;
            if (initiative != null)
            {
                _initiativeScheduler.Completed(_initiativeEnvironment.Now(), completed || !string.IsNullOrWhiteSpace(answer?.Text));
                if (completed) _initiativeError = "";
            }
            _initiativeSending = false;
            if (!_closing)
            {
                _memoryNextAt = DateTimeOffset.UtcNow.AddSeconds(3);
                SetBusy(false); RenderMessages(); UpdateInitiativeStatus();
                if (initiative == null) { InputBox.Focus(); _ = Dispatcher.BeginInvoke(async () => await TickInitiativeAsync()); }
            }
        }
    }
    private void OnStop(object sender, RoutedEventArgs e)
    {
        _pendingInitiative = null;
        if (!_initiativeSending) _initiativeScheduler.CancelPending(_initiativeEnvironment.Now());
        _chatCts?.Cancel(); _voiceCts?.Cancel(); StopPlayback(); UpdateStopButton();
    }
    private void UpdateStopButton() => StopButton.IsEnabled = _busy || _voiceCts != null || _playingFile != null;
    private void OnClearChat(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (MessageBox.Show("清空当前朋友的聊天记录？长期记忆会保留。此操作不可撤销。", "清空聊天", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Guard(() => { CancelMemoryLearning(); Friend.Messages.Clear(); Friend.Learning = new(); Save(true); RenderMessages(); ResetInitiative(true); UpdateMemoryLearningUi(); Notice("聊天记录已清空"); });
    }

    private void LoadSettings()
    {
        if (!_ready) return;
        SyncVoiceSelection();
        RoleName.Text = Friend.Name; RoleTagline.Text = Friend.Tagline; RolePersona.Text = Friend.Persona; RoleGreeting.Text = Friend.Greeting;
        var s = _state.Settings;
        LoadInterfaceTheme();
        ModelEndpoint.Text = s.Endpoint; ModelName.Text = s.Model; ModelKey.Password = "";
        ModelTemperature.Text = s.Temperature.ToString(CultureInfo.InvariantCulture);
        ContextCount.Text = s.ContextMessages.ToString(); MemoryBudget.Text = s.MemoryBudget.ToString(); SuggestMemory.IsChecked = s.SuggestMemories;
        AutoMemoryEnabled.IsChecked = s.AutoMemoryEnabled;
        AutoSummaryEnabled.IsChecked = s.AutoSummaryEnabled;
        var v = Friend.Voice;
        VoiceCharacterName.Text = Friend.Name;
        JapaneseMode.IsChecked = Friend.JapaneseChat.Enabled;
        ChatDisplayLanguage.Text = Friend.JapaneseChat.DisplayLanguage;
        VoiceEnabled.IsChecked = v.Enabled; VoiceAutoPlay.IsChecked = v.AutoPlay;
        SetCombo(VoiceProvider, v.Provider switch
        {
            "GPT-SoVITS" => "GPT-SoVITS 外接",
            "OpenAI 兼容" => "OpenAI-compatible",
            "Custom" or "自定义 JSON" => "Custom JSON",
            _ => v.Provider
        });
        VoiceEndpoint.Text = v.Endpoint; VoiceModel.Text = v.Model; VoiceName.Text = v.Voice; VoiceLanguage.Text = v.Language;
        VoiceReference.Text = v.ReferenceAudio; VoiceReferenceText.Text = v.ReferenceText; VoiceGptWeights.Text = v.GptWeights; VoiceSovitsWeights.Text = v.SovitsWeights;
        VoiceKey.Password = ""; VoiceTemplate.Text = v.RequestTemplate;
        UpdateVoiceProviderUi();
        CacheLimit.Text = s.CacheLimitMb.ToString(); CacheHours.Text = s.CacheHours.ToString(); StoragePath.Text = _store.Root;
        InitiativeIdleEnabled.IsChecked = s.Initiative.IdleEnabled;
        InitiativeActivityEnabled.IsChecked = s.Initiative.ActivityEnabled;
        InitiativeMaxUnanswered.Text = s.Initiative.MaxUnanswered.ToString();
        UpdateInitiativeStatus();
    }
    private static int ParseInt(TextBox box, int min, int max, string label)
    {
        if (!int.TryParse(box.Text, out int v) || v < min || v > max) throw new InvalidOperationException($"{label}应在 {min}～{max} 之间。");
        return v;
    }
    private bool ApplySettings()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(RoleName.Text) || RoleName.Text.Length > 32) throw new InvalidOperationException("角色名应为 1～32 个字符。");
            if (string.IsNullOrWhiteSpace(RolePersona.Text) || RolePersona.Text.Length > 30000) throw new InvalidOperationException("人物设定应为 1～30000 个字符。");
            if (!double.TryParse(ModelTemperature.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double temp) || temp < 0 || temp > 2 || !double.IsFinite(temp)) throw new InvalidOperationException("温度应为 0～2 的小数。");
            int context = ParseInt(ContextCount, 2, 200, "上下文条数"), budget = ParseInt(MemoryBudget, 200, 12000, "记忆字符预算"), limit = ParseInt(CacheLimit, 16, 4096, "缓存上限 MB"), hours = ParseInt(CacheHours, 1, 720, "缓存保留小时");
            int unanswered = ParseInt(InitiativeMaxUnanswered, 1, 10, "连续未回复上限");
            string displayLanguage = ChatDisplayLanguage.Text.Trim();
            if (displayLanguage.Length is < 1 or > 80 || displayLanguage.Any(char.IsControl)) throw new InvalidOperationException("显示语言应为 1～80 个字符的语言名称。");
            var japaneseChat = new JapaneseChatSettings { Enabled = JapaneseMode.IsChecked == true, DisplayLanguage = displayLanguage };
            var modelUri = new Uri(ModelEndpoint.Text.Trim());
            if (modelUri.Scheme != "https" && !(modelUri.Scheme == "http" && modelUri.IsLoopback)) throw new InvalidOperationException("远程服务请使用 HTTPS，本机服务可使用 HTTP。");
            var s = _state.Settings;
            string key = ModelKey.Password == "-" ? "" : ModelKey.Password.Length > 0 ? SecretVault.Protect(ModelKey.Password) : s.ProtectedApiKey;
            string voiceKey = VoiceKey.Password == "-" ? "" : VoiceKey.Password.Length > 0 ? SecretVault.Protect(VoiceKey.Password) : Friend.Voice.ProtectedApiKey;
            var voice = new VoiceSettings
            {
                Enabled = VoiceEnabled.IsChecked == true, AutoPlay = VoiceAutoPlay.IsChecked == true, Provider = Combo(VoiceProvider),
                Endpoint = VoiceEndpoint.Text.Trim(), Model = VoiceModel.Text.Trim(), Voice = VoiceName.Text.Trim(), Language = VoiceLanguage.Text.Trim(),
                ReferenceAudio = VoiceReference.Text.Trim(), ReferenceText = VoiceReferenceText.Text, GptWeights = VoiceGptWeights.Text.Trim(),
                SovitsWeights = VoiceSovitsWeights.Text.Trim(), ProtectedApiKey = voiceKey, RequestTemplate = VoiceTemplate.Text
            };
            bool voiceChanged = JsonSerializer.Serialize(Friend.Voice) != JsonSerializer.Serialize(voice)
                || JsonSerializer.Serialize(Friend.JapaneseChat) != JsonSerializer.Serialize(japaneseChat);
            CancelMemoryLearning();
            Friend.Name = RoleName.Text.Trim(); Friend.Tagline = RoleTagline.Text.Trim(); Friend.Persona = RolePersona.Text; Friend.Greeting = RoleGreeting.Text;
            s.Endpoint = modelUri.ToString().TrimEnd('/'); s.Model = ModelName.Text.Trim(); s.ProtectedApiKey = key; s.Temperature = temp; s.ContextMessages = context; s.MemoryBudget = budget; s.SuggestMemories = SuggestMemory.IsChecked == true;
            s.CacheLimitMb = limit; s.CacheHours = hours;
            s.AutoMemoryEnabled = AutoMemoryEnabled.IsChecked == true;
            s.AutoSummaryEnabled = AutoSummaryEnabled.IsChecked == true;
            s.Initiative.IdleEnabled = InitiativeIdleEnabled.IsChecked == true;
            s.Initiative.ActivityEnabled = InitiativeActivityEnabled.IsChecked == true;
            s.Initiative.MaxUnanswered = unanswered;
            Friend.Voice = voice;
            Friend.JapaneseChat = japaneseChat;
            Save(); ModelKey.Clear(); VoiceKey.Clear(); RefreshFriends(); RenderFriend(); RefreshStorage();
            if (voiceChanged) CancelVoiceWork();
            VoiceCharacterName.Text = Friend.Name;
            return true;
        }
        catch (Exception e) { Notice(Boot.SafeError(e)); return false; }
    }
    private void OnSaveSettings(object sender, RoutedEventArgs e) { if (ApplySettings()) Notice("已保存"); }
    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        if (!ApplySettings()) return;
        var button = (Button)sender; button.IsEnabled = false;
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)); Notice(await _chat.TestAsync(_state.Settings, cts.Token)); }
        catch (Exception ex) { Notice(Boot.SafeError(ex)); }
        finally { button.IsEnabled = true; }
    }
    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Guard(() => { var profile = new FriendProfile { Id = Guid.NewGuid().ToString("N"), Name = "新角色", Tagline = "", Persona = "以自然简洁的语言交流。", Greeting = "你好。" }; _state.Friends.Add(profile); _state.SelectedFriendId = profile.Id; Save(); RefreshFriends(); LoadSettings(); OnNavigate(SettingsNavButton, new RoutedEventArgs()); Notice("角色已创建"); });
    }
    private void OnDeleteFriend(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_state.Friends.Count == 1) { Notice("至少保留一个角色"); return; }
        if (MessageBox.Show($"删除「{Friend.Name}」及其全部聊天与记忆？此操作不可撤销。", "删除角色", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Guard(() => { _state.Friends.Remove(Friend); _state.SelectedFriendId = _state.Friends[0].Id; Save(true); RefreshFriends(); LoadSettings(); RenderFriend(); });
    }
    private void PickImage(string kind)
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = kind switch
            { "portrait" => "选择立绘", "background" => "选择聊天背景", _ => "选择聊天头像" } };
        if (dialog.ShowDialog(this) != true) return;
        Guard(() => ApplyImage(dialog.FileName, kind));
    }
    private void ApplyImage(string source, string kind)
    {
        if (kind is not ("avatar" or "portrait" or "background")) throw new ArgumentException("图片用途无效。", nameof(kind));
        var path = _store.ImportAsset(source);
        try
        {
            if (LoadImage(path) == null) throw new InvalidOperationException("无法解码图片，请使用有效的 PNG、JPEG、BMP 或 GIF 文件。");
            if (kind == "background") Friend.ChatBackgroundPath = path;
            else if (kind == "portrait") Friend.PortraitPath = path;
            else Friend.AvatarPath = path;
            Save();
        }
        catch
        {
            try { _store.PruneAssets(_state); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        RenderFriend(); Notice("已更新");
    }
    private void OnPickAvatar(object sender, RoutedEventArgs e) => PickImage("avatar");
    private void OnPickPortrait(object sender, RoutedEventArgs e) => PickImage("portrait");
    private void OnPickBackground(object sender, RoutedEventArgs e) => PickImage("background");
    private void OnRemoveAvatar(object sender, RoutedEventArgs e) => Guard(() => { Friend.AvatarPath = ""; Save(); RenderFriend(); });
    private void OnRemovePortrait(object sender, RoutedEventArgs e) => Guard(() => { Friend.PortraitPath = ""; Save(); RenderFriend(); });
    private void OnRemoveBackground(object sender, RoutedEventArgs e) => Guard(() => { Friend.ChatBackgroundPath = ""; Save(); RenderFriend(); });
    private void OnExportFriend(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "角色数据 JSON|*.json", FileName = "致电-角色备份.json", InitialDirectory = _store.Root };
        if (dialog.ShowDialog(this) == true) Guard(() => { _store.Export(Friend, dialog.FileName); Notice("已导出：角色、聊天、记忆（不含密钥及图片）"); });
    }
    private void OnImportFriend(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Filter = "朋友数据 JSON|*.json" };
        if (dialog.ShowDialog(this) == true) Guard(() => { var profile = _store.ImportFriend(dialog.FileName); _state.Friends.Add(profile); _state.SelectedFriendId = profile.Id; Save(); RefreshFriends(); LoadSettings(); RenderFriend(); Notice("角色已导入"); });
    }

    private sealed record MemoryRow(MemoryItem Item)
    {
        public string Content => (Item.Pinned ? "★ " : "") + Item.Content;
        public string Category => Item.Category;
        public string State => Item.State;
        public string StateLabel => Item.State == "pending" ? "待确认" : Item.State == "archived" ? "已归档" : Item.Origin == "model" ? "自动记忆" : "已确认";
    }
    private void RefreshMemories()
    {
        if (!_ready) return;
        string query = MemorySearch.Text.Trim();
        var selectedId = _selectedMemory?.Id;
        var rows = Friend.Memories.Where(x => query.Length == 0 || x.Content.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Category.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.State == "pending" ? 0 : x.State == "approved" ? 1 : 2).ThenByDescending(x => x.Pinned).ThenByDescending(x => x.UpdatedAt).Select(x => new MemoryRow(x)).ToList();
        MemoryList.ItemsSource = rows;
        MemoryList.SelectedItem = rows.FirstOrDefault(x => x.Item.Id == selectedId);
        if (MemoryList.SelectedItem == null) ResetMemoryEditor();
        UpdateMemoryLearningUi();
    }
    private void OnMemorySearch(object sender, TextChangedEventArgs e) { if (_ready) RefreshMemories(); }
    private void OnMemorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || MemoryList.SelectedItem is not MemoryRow row) return;
        _selectedMemory = row.Item; MemoryContent.Text = row.Item.Content; SetCombo(MemoryCategory, row.Item.Category); MemoryPinned.IsChecked = row.Item.Pinned; MemoryExpiry.Text = row.Item.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
        var source = Friend.Messages.FirstOrDefault(x => x.Id == row.Item.SourceMessageId);
        MemoryDetails.Text = $"写入方式：{(row.Item.Origin == "model" ? "模型整理" : "手动 / 指令提取")}\n创建：{row.Item.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}\n更新：{row.Item.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}\n最近引用：{row.Item.LastUsedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "尚未引用"}\n来源：{(source == null ? "手动录入 / 来源已删除" : source.Text.Length > 160 ? source.Text[..160] + "…" : source.Text)}\n仅已生效、未过期的记忆会进入对话。";
    }
    private void ResetMemoryEditor() { _selectedMemory = null; MemoryContent.Text = ""; SetCombo(MemoryCategory, "偏好"); MemoryPinned.IsChecked = false; MemoryExpiry.Text = ""; MemoryDetails.Text = ""; }
    private void OnNewMemory(object sender, RoutedEventArgs e) { MemoryList.SelectedItem = null; ResetMemoryEditor(); MemoryContent.Focus(); }
    private bool StoreMemory(bool approve)
    {
        if (_busy) return false;
        try
        {
            string content = MemoryContent.Text.Trim();
            if (content.Length == 0 || content.Length > 4000) throw new InvalidOperationException("记忆内容应为 1～4000 个字符。");
            DateTimeOffset? expiry = null;
            if (!string.IsNullOrWhiteSpace(MemoryExpiry.Text))
            {
                if (!DateTime.TryParseExact(MemoryExpiry.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new InvalidOperationException("到期日期格式应为 yyyy-MM-dd，或留空。");
                expiry = new DateTimeOffset(DateTime.SpecifyKind(date.AddDays(1).AddTicks(-1), DateTimeKind.Local));
            }
            var memory = _selectedMemory ?? new MemoryItem();
            CancelMemoryLearning();
            if (_selectedMemory != null && content != memory.Content) MemoryLearning.SuppressSource(Friend, memory.SourceMessageId);
            memory.Origin = "manual";
            memory.Content = content; memory.Category = Combo(MemoryCategory); memory.Pinned = MemoryPinned.IsChecked == true; memory.ExpiresAt = expiry; memory.UpdatedAt = DateTimeOffset.Now;
            if (approve) memory.State = "approved";
            if (_selectedMemory == null) Friend.Memories.Add(memory);
            _selectedMemory = memory; Save(); RefreshMemories(); Notice(approve ? "记忆已确认" : "记忆已保存"); return true;
        }
        catch (Exception ex) { Notice(Boot.SafeError(ex)); return false; }
    }
    private void OnSaveMemory(object sender, RoutedEventArgs e) => StoreMemory(false);
    private void OnApproveMemory(object sender, RoutedEventArgs e) => StoreMemory(true);
    private void OnArchiveMemory(object sender, RoutedEventArgs e) => Guard(() => { if (_busy || _selectedMemory == null) return; CancelMemoryLearning(); MemoryLearning.SuppressSource(Friend, _selectedMemory.SourceMessageId); _selectedMemory.State = "archived"; _selectedMemory.UpdatedAt = DateTimeOffset.Now; Save(); RefreshMemories(); Notice("已归档"); });
    private void OnDeleteMemory(object sender, RoutedEventArgs e)
    {
        if (_busy || _selectedMemory == null) return;
        var choice = MessageBox.Show("删除这条记忆？\n\n是：同时删除来源消息及紧随的回复。\n否：仅删除记忆，保留聊天原文。\n取消：不删除。", "删除记忆", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        Guard(() =>
        {
            CancelMemoryLearning();
            MemoryLearning.SuppressSource(Friend, _selectedMemory.SourceMessageId);
            if (choice == MessageBoxResult.Yes && _selectedMemory.SourceMessageId.Length > 0)
            {
                int index = Friend.Messages.FindIndex(x => x.Id == _selectedMemory.SourceMessageId);
                if (index >= 0) { Friend.Messages.RemoveAt(index); if (index < Friend.Messages.Count && Friend.Messages[index].Role == "assistant") Friend.Messages.RemoveAt(index); }
            }
            Friend.Memories.Remove(_selectedMemory); _selectedMemory = null; Save(true); RefreshMemories(); RenderMessages(); Notice("已删除并更新备份；导出文件及损坏副本需手动处理");
        });
    }

    private void OnPickReference(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "参考音频|*.wav;*.mp3;*.flac;*.ogg" };
        if (dialog.ShowDialog(this) == true) VoiceReference.Text = dialog.FileName;
    }
    private void OnPickGptWeights(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "GPT 权重|*.ckpt", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) VoiceGptWeights.Text = dialog.FileName;
    }
    private void OnPickSovitsWeights(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SoVITS 权重|*.pth", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) VoiceSovitsWeights.Text = dialog.FileName;
    }
    private void OnVoiceProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) UpdateVoiceProviderUi();
    }
    private void UpdateVoiceProviderUi()
    {
        if (!_ready) return;
        string provider = Combo(VoiceProvider);
        bool embedded = provider == "GPT-SoVITS 内置";
        VoiceLanguageLabel.Text = embedded ? "参考音频语言" : "常规 / 参考语言";
        bool gpt = embedded || provider is "GPT-SoVITS" or "GPT-SoVITS 外接";
        VoiceConnectionPanel.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        VoiceStandardFields.Visibility = provider is "OpenAI-compatible" or "OpenAI 兼容" ? Visibility.Visible : Visibility.Collapsed;
        VoiceGptPanel.Visibility = gpt ? Visibility.Visible : Visibility.Collapsed;
        VoiceTemplatePanel.Visibility = provider is "Custom" or "Custom JSON" or "自定义 JSON" ? Visibility.Visible : Visibility.Collapsed;
        EmbeddedVoiceStatus.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
        if (embedded)
        {
            var root = Path.Combine(_store.Root, "engines", "gpt-sovits");
            bool present = File.Exists(Path.Combine(root, "runtime", "python.exe"))
                && File.Exists(Path.Combine(root, "GPT_SoVITS", "TTS_infer_pack", "TTS.py"))
                && File.Exists(Path.Combine(_store.Root, "VoiceEngine", "gpt_sovits_worker.py"));
            EmbeddedVoiceStatus.Text = _voice.IsEmbeddedRunning ? "内置引擎运行中" : present ? "内置引擎待机" : "内置引擎文件缺失";
        }
    }
    private async void OnMountVoice(object sender, RoutedEventArgs e)
    {
        if (!ApplySettings()) return;
        await RunVoiceAsync(Friend, null, test: true);
    }
    private async void OnTestVoice(object sender, RoutedEventArgs e)
    {
        if (!ApplySettings()) return;
        if (Friend.JapaneseChat.Enabled)
            await RunVoiceAsync(Friend, "こんにちは。今日はどんな一日でしたか。", true, "こんにちは。今日はどんな一日でしたか。");
        else await SpeakAsync("你好，我是" + Friend.Name + "。很高兴听见你的声音。", true);
    }
    private Task SpeakAsync(string text, bool test = false) => SpeakForFriendAsync(Friend, text, test);
    private Task SpeakForFriendAsync(FriendProfile friend, string text, bool test = false) => RunVoiceAsync(friend, text, test);
    private Task SpeakMessageAsync(FriendProfile friend, ChatMessage message) => RunVoiceAsync(friend, message.Text, false, message.JapaneseText);
    private async Task RunVoiceAsync(FriendProfile friend, string? text, bool test, string? japaneseText = null)
    {
        if (_closing || !ReferenceEquals(friend, Friend)) return;
        if (_cleanCacheAfterVoice) { Notice("正在清理缓存"); return; }
        var settings = friend.Voice.Copy();
        bool japanese = friend.JapaneseChat.Enabled;
        if (!settings.Enabled && !test) { Notice("当前角色未启用语音"); return; }
        if (_voiceCts is { IsCancellationRequested: false }) { Notice("语音处理中"); return; }
        StopPlayback();
        using var cts = new CancellationTokenSource();
        _voiceCts = cts;
        long epoch = _voiceEpoch;
        string? generated = null;
        bool Current() => !_closing && epoch == _voiceEpoch && ReferenceEquals(friend, Friend) && ReferenceEquals(_voiceCts, cts);
        UpdateStopButton();
        try
        {
            Notice(text == null ? "正在加载模型" : "正在合成语音");
            if (text == null)
            {
                await _voice.MountAsync(settings, cts.Token);
                if (Current() && !cts.IsCancellationRequested) Notice("语音模型已加载");
                return;
            }
            if (japanese)
            {
                if (string.IsNullOrWhiteSpace(japaneseText))
                {
                    Notice("正在准备日语语音");
                    japaneseText = await _chat.TranslateAsync(_state.Settings, friend, text, "日本語", cts.Token);
                }
                JapaneseChat.EnsureJapanese(japaneseText);
                text = japaneseText;
                cts.Token.ThrowIfCancellationRequested();
                if (!Current()) return;
            }
            Notice("正在合成语音");
            generated = await _voice.SynthesizeAsync(settings, text, _store.CacheDirectory, cts.Token, japanese: japanese);
            if (!Current() || cts.IsCancellationRequested) return;
            _playingFile = generated; generated = null;
            _player.Open(new Uri(_playingFile)); _player.Play(); Notice("播放中");
        }
        catch (Exception ex)
        {
            if (Current()) { StopPlayback(); Notice(ex is OperationCanceledException ? "" : Boot.SafeError(ex)); }
        }
        finally
        {
            DeleteVoiceFile(generated);
            if (ReferenceEquals(_voiceCts, cts)) _voiceCts = null;
            _lastVoiceActivity = DateTimeOffset.UtcNow;
            if (!_closing)
            {
                if (_cleanCacheAfterVoice && _voiceCts == null)
                {
                    _cleanCacheAfterVoice = false;
                    StopPlayback(); _voice.StopEmbedded(); TryClean(true);
                }
                UpdateStopButton(); UpdateVoiceProviderUi();
            }
        }
    }
    private void SyncVoiceSelection()
    {
        if (ReferenceEquals(_voiceSelection, Friend)) return;
        CancelVoiceWork();
        _voiceSelection = Friend;
    }
    private void CancelVoiceWork()
    {
        _voiceEpoch++;
        _voiceCts?.Cancel();
        StopPlayback();
    }
    private static void DeleteVoiceFile(string? file)
    {
        if (file != null) try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private void StopPlayback()
    {
        _player.Stop(); _player.Close();
        DeleteVoiceFile(_playingFile); _playingFile = null;
        UpdateStopButton();
    }
    private void RefreshStorage()
    {
        var sizes = _store.GetStorageBytes();
        StorageStats.Text = $"用户数据 {sizes.Data / 1048576d:F2} MB     ·     可清理缓存 {sizes.Cache / 1048576d:F2} MB";
    }
    private void TryClean(bool all)
    {
        Guard(() => { long freed = _store.CleanupCache(_state.Settings, all); RefreshStorage(); if (all) Notice($"已清理 {freed / 1048576d:F2} MB 缓存。角色、记忆与聊天记录已保留。"); });
    }
    private void OnCleanCache(object sender, RoutedEventArgs e)
    {
        if (_voiceCts != null)
        {
            _cleanCacheAfterVoice = true;
            _voiceCts.Cancel();
            Notice("正在停止语音并清理缓存");
            return;
        }
        StopPlayback(); _voice.StopEmbedded(); TryClean(true); UpdateVoiceProviderUi();
    }
    private void OnOpenData(object sender, RoutedEventArgs e) => Guard(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _store.DataDirectory, UseShellExecute = true }));
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _memoryTimer.Stop(); CancelMemoryLearning();
        StopInitiative();
        _janitor.Stop(); _chatCts?.Cancel(); _voiceCts?.Cancel(); StopPlayback();
        foreach (var message in _state.Friends.SelectMany(f => f.Messages).Where(m => m.Status == "streaming")) message.Status = "interrupted";
        try { Save(); }
        catch
        {
            if (MessageBox.Show("保存失败，最近修改可能未写入磁盘。仍然退出？", "致电", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true; _closing = false; _janitor.Start();
                if (_initiativeEnabled) _initiativeTimer.Start();
                if (_memoryLearningEnabled) _memoryTimer.Start();
                RefreshFriends(); LoadSettings(); RenderFriend(); RefreshMemories();
                return;
            }
        }
        _chat.Dispose(); _voice.Dispose();
    }
}
