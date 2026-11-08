using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Qiban.Core;
using Qiban.Desktop;

namespace Qiban.DesktopTests;

internal static partial class Program
{
    private static async Task VerifyInterfaceThemeAsync(string root)
    {
        var store = new PortableStore(root);
        store.Initialize();
        var state = new AppState();
        state.Friends[0].Messages =
        [
            new() { Role = "user", Text = "今天想试试新的界面。" },
            new() { Role = "assistant", Text = "蓝色的窗口，很有怀念的感觉呢。你喜欢哪一种？" }
        ];
        state.Friends[0].Memories.Add(new MemoryItem { Content = "喜欢经典桌面的蓝色界面", State = "approved" });
        store.Save(state);
        MainWindow? window = Open();
        try
        {
            await Idle();
            var selector = Control<ComboBox>("InterfaceThemeBox");
            int standardDictionaryCount = window.Resources.MergedDictionaries.Count;
            var firstBubble = Bubble(0);
            var secondBubble = Bubble(1);
            var standardBackground = firstBubble.Background.ToString();
            var standardActiveTitle = ((Brush)window.FindResource("CaptionBrush")).ToString();
            var standardInactiveTitle = ((Brush)window.FindResource("InactiveCaptionBrush")).ToString();
            bool initiallyActive = window.IsActive;
            var standardChat = ((Brush)window.FindResource("ChatBrush")).ToString();
            var originalFriend = JsonSerializer.Serialize(state.Friends[0]);
            var originalSettings = JsonSerializer.Serialize(state.Settings);
            Check(state.Settings.InterfaceTheme == "standard" && SelectedTheme() == "standard"
                && window.FindResource("CaptionBrush") is SolidColorBrush
                && firstBubble.CornerRadius == new CornerRadius(15),
                "a fresh window and theme selector use the standard appearance");
            Capture(window, Path.Combine(root, "theme-standard.png"));

            // Switching appearance must not implicitly save or discard edits in other settings.
            Click(Control<Button>("SettingsNavButton"));
            Control<TextBox>("RoleName").Text = "未保存的角色名";
            Control<TextBox>("ModelTemperature").Text = "尚未填写完成";
            Control<TextBox>("InputBox").Text = "这条输入尚未发送\n第二行";
            Select("xp");
            await Idle();
            Check(state.Settings.InterfaceTheme == "xp" && store.Load().Settings.InterfaceTheme == "xp"
                && window.FindResource("CaptionBrush") is LinearGradientBrush
                && Control<Grid>("WindowTitleBar").Background is LinearGradientBrush,
                "selecting Windows XP immediately updates the actual caption and persists without a separate save");
            Check(Control<TextBox>("InputBox").Text == "这条输入尚未发送\n第二行"
                && Control<TextBox>("RoleName").Text == "未保存的角色名"
                && Control<TextBox>("ModelTemperature").Text == "尚未填写完成"
                && JsonSerializer.Serialize(state.Friends[0]) == originalFriend
                && JsonSerializer.Serialize(store.Load().Friends[0]) == originalFriend,
                "theme switching preserves the chat draft, unfinished settings, character, messages and long-term memories");
            var persistedSettings = store.Load().Settings;
            persistedSettings.InterfaceTheme = "standard";
            Check(JsonSerializer.Serialize(persistedSettings) == originalSettings,
                "theme selection commits only the global appearance preference, not unrelated settings edits");
            Check(ReferenceEquals(firstBubble, Bubble(0)) && ReferenceEquals(secondBubble, Bubble(1))
                && firstBubble.CornerRadius == new CornerRadius(3)
                && firstBubble.Background.ToString() != standardBackground
                && ((Brush)window.FindResource("ChatBrush")).ToString() != standardChat
                && secondBubble.CornerRadius == new CornerRadius(3),
                "existing user and assistant bubbles update in place without replacing conversation elements");

            // Exercise the same renderer used when a streamed answer starts, without calling a model.
            var rendered = (TextBlock)typeof(MainWindow).GetMethod("AddBubble", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [new ChatMessage { Role = "assistant", Text = "新的消息也会使用当前主题。" }, false])!;
            var newBubble = (Border)rendered.Parent;
            Check(newBubble.CornerRadius == new CornerRadius(3)
                && newBubble.Background.ToString() == secondBubble.Background.ToString(),
                "newly rendered replies use the currently selected Windows XP appearance");

            var appearance = Descendants(window).OfType<TabItem>().Single(tab => Equals(tab.Header, "外观"));
            appearance.IsSelected = true;
            await Idle();
            Check(selector.IsVisible && selector.ActualWidth > 100 && selector.ActualHeight >= 25,
                "appearance settings expose a visible usable theme selector");
            selector.ApplyTemplate();
            var dropdown = (ToggleButton)selector.Template.FindName("DropDownButton", selector);
            var popup = (Popup)selector.Template.FindName("PART_Popup", selector);
            // A Popup can be clamped onto a visible monitor even when its owner is offscreen.
            // Keep this interaction fixture invisible while exercising its real layout.
            popup.Child.Opacity = 0;
            typeof(ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dropdown, null);
            await Idle();
            Check(selector.IsDropDownOpen && popup.IsOpen && dropdown.IsChecked == true
                && selector.Items.OfType<ComboBoxItem>().All(item => item.IsVisible && item.ActualWidth > 100 && item.ActualHeight > 0),
                "the XP dropdown button click opens a laid-out popup with both theme options visible");
            ThemeKey(selector, Key.Home);
            ThemeKey(selector, Key.Enter);
            await Idle();
            Check(SelectedTheme() == "standard" && store.Load().Settings.InterfaceTheme == "standard"
                && !selector.IsDropDownOpen && !popup.IsOpen,
                "selecting and confirming another theme closes the XP dropdown as the control template changes");
            Select("xp");
            await Idle();

            var voiceTab = Descendants(window).OfType<TabItem>().Single(tab => Equals(tab.Header, "角色声音"));
            voiceTab.IsSelected = true;
            var japanese = Control<CheckBox>("JapaneseMode");
            var language = Control<ComboBox>("ChatDisplayLanguage");
            bool? originalJapaneseMode = japanese.IsChecked;
            string originalLanguage = language.Text;
            japanese.IsChecked = true;
            await Idle();
            language.ApplyTemplate();
            var editor = (TextBox)language.Template.FindName("PART_EditableTextBox", language);
            editor.SelectAll();
            editor.SelectedText = "Español";
            await Idle();
            Check(language.IsEditable && language.IsEnabled && editor.IsVisible && language.Text == "Español"
                && state.Friends[0].JapaneseChat.Enabled == false && state.Friends[0].JapaneseChat.DisplayLanguage == originalLanguage,
                "the XP editable language field accepts custom text without changing unsaved Japanese-mode settings");
            Capture(window, Path.Combine(root, "theme-xp-voice.png"));
            Select("standard");
            Select("xp");
            await Idle();
            Check(language.Text == "Español" && japanese.IsChecked == true && language.IsEnabled
                && ((TextBox)language.Template.FindName("PART_EditableTextBox", language)).Text == "Español",
                "editable language text and Japanese-mode selection survive both control-template swaps");
            japanese.IsChecked = false;
            await Idle();
            Check(!language.IsEnabled && language.Text == "Español" && JsonSerializer.Serialize(state.Friends[0]) == originalFriend,
                "the XP language selector still follows the Japanese-mode checkbox without mutating the character");
            japanese.IsChecked = originalJapaneseMode;
            language.Text = originalLanguage;
            appearance.IsSelected = true;
            await Idle();
            Capture(window, Path.Combine(root, "theme-xp-settings.png"));
            Click(Control<Button>("ChatNavButton"));
            await Idle();
            Capture(window, Path.Combine(root, "theme-xp.png"));
            Click(Control<Button>("MemoryNavButton"));
            Control<ListBox>("MemoryList").SelectedIndex = 0;
            await Idle();
            Capture(window, Path.Combine(root, "theme-xp-memory.png"));
            Click(Control<Button>("SettingsNavButton"));
            Descendants(window).OfType<TabItem>().Single(tab => Equals(tab.Header, "角色")).IsSelected = true;
            await Idle();
            Capture(window, Path.Combine(root, "theme-xp-role.png"));
            Click(Control<Button>("ChatNavButton"));

            window.Opacity = 0; // Native maximize may put this offscreen fixture onto a monitor.
            Click(Control<Button>("MaximizeWindowButton"));
            await UntilAsync(() => window.WindowState == WindowState.Maximized, "maximize XP fixture");
            Click(Control<Button>("MaximizeWindowButton"));
            await UntilAsync(() => window.WindowState == WindowState.Normal, "restore XP fixture");
            window.Left = -20000;
            window.Top = 0;
            window.Opacity = 1;
            await Idle();
            Check(state.Settings.InterfaceTheme == "xp" && newBubble.CornerRadius == new CornerRadius(3)
                && Control<Grid>("WindowTitleBar").Background is LinearGradientBrush,
                "native maximize and restore preserve the XP theme and active chat elements");

            Select("standard");
            await Idle();
            // A native maximize/restore command can activate an initially inactive hidden
            // fixture. Match the restored palette to the current window activation state.
            string? expectedTitle = window.IsActive ? standardActiveTitle : standardInactiveTitle;
            string? restoredTitle = Control<Grid>("WindowTitleBar").Background.ToString();
            string persistedTheme = store.Load().Settings.InterfaceTheme;
            Check(persistedTheme == "standard"
                && firstBubble.CornerRadius == new CornerRadius(15)
                && newBubble.CornerRadius == new CornerRadius(15)
                && firstBubble.Background.ToString() == standardBackground
                && restoredTitle == expectedTitle
                && ((Brush)window.FindResource("ChatBrush")).ToString() == standardChat
                && ReferenceEquals(firstBubble, Bubble(0)),
                "switching back restores the original colors and geometry for old and new messages in place " +
                $"(theme={persistedTheme}, radii={firstBubble.CornerRadius}/{newBubble.CornerRadius}, " +
                $"bubble={firstBubble.Background}/{standardBackground}, active={initiallyActive}->{window.IsActive}, " +
                $"caption={restoredTitle}/{expectedTitle}, chat={window.FindResource("ChatBrush")}/{standardChat}, " +
                $"same element={ReferenceEquals(firstBubble, Bubble(0))})");
            for (int i = 0; i < 3; i++) { Select("xp"); Select("standard"); }
            Check(window.Resources.MergedDictionaries.Count == standardDictionaryCount && state.Settings.InterfaceTheme == "standard",
                "repeated theme switches do not accumulate resource dictionaries");

            string backgroundSource = Path.Combine(root, "background.png");
            CreateBackgroundFixture(backgroundSource, 900, 600);
            typeof(MainWindow).GetMethod("ApplyImage", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [backgroundSource, "background"]);
            var customBackground = Control<Border>("ChatBackgroundLayer").Background;
            originalFriend = JsonSerializer.Serialize(state.Friends[0]);
            window.Width = 940;
            window.Height = 680;
            foreach (string theme in new[] { "standard", "xp" })
            {
                Select(theme);
                Check(ReferenceEquals(customBackground, Control<Border>("ChatBackgroundLayer").Background)
                    && customBackground is ImageBrush { ImageSource: not null },
                    $"{theme} preserves the character's custom chat background");
                foreach (string nav in new[] { "ChatNavButton", "MemoryNavButton", "SettingsNavButton" })
                {
                    Click(Control<Button>(nav));
                    if (nav == "SettingsNavButton") appearance.IsSelected = true;
                    await Idle();
                    Check(window.IsMeasureValid && window.IsArrangeValid
                        && (nav != "ChatNavButton" || Control<TextBox>("InputBox").ActualWidth > 200)
                        && (nav != "SettingsNavButton" || selector.IsVisible && Inside(selector)),
                        $"{theme} {nav} remains usable at minimum window size");
                    if (theme == "xp" && nav == "ChatNavButton")
                        Capture(window, Path.Combine(root, "theme-xp-background.png"));
                }
            }
            Capture(window, Path.Combine(root, "theme-xp-settings-minimum.png"));
            window.Close();
            window = null;
            store = new PortableStore(root);
            store.Initialize();
            state = store.Load();
            window = Open();
            await Idle();
            Check(state.Settings.InterfaceTheme == "xp" && SelectedTheme() == "xp"
                && Control<Grid>("WindowTitleBar").Background is LinearGradientBrush
                && Bubble(0).CornerRadius == new CornerRadius(3)
                && Control<Border>("ChatBackgroundLayer").Background is ImageBrush { ImageSource: not null }
                && JsonSerializer.Serialize(state.Friends[0]) == originalFriend,
                "reopening restores the selected XP theme alongside unchanged conversation and memory data");
        }
        finally { window?.Close(); }

        MainWindow Open()
        {
            var opened = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                Width = 1140, Height = 780, ShowInTaskbar = false, ShowActivated = false
            };
            opened.Show();
            return opened;
        }
        T Control<T>(string name) where T : FrameworkElement => (T)window!.FindName(name);
        string? SelectedTheme() => (Control<ComboBox>("InterfaceThemeBox").SelectedItem as ComboBoxItem)?.Tag as string;
        void Select(string theme) => Control<ComboBox>("InterfaceThemeBox").SelectedItem =
            Control<ComboBox>("InterfaceThemeBox").Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, theme));
        Border Bubble(int index) => Descendants(Control<StackPanel>("MessagesPanel").Children[index]).OfType<Border>().Single();
        bool Inside(FrameworkElement element)
        {
            Point origin = element.TranslatePoint(new Point(), window);
            return origin.X >= 0 && origin.Y >= 0 && origin.X + element.ActualWidth <= window!.ActualWidth + 1
                && origin.Y + element.ActualHeight <= window.ActualHeight + 1;
        }
        async Task Idle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window!.UpdateLayout();
        }
    }

    private static void ThemeKey(UIElement element, Key key)
    {
        var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyDownEvent };
        element.RaiseEvent(keyEvent);
    }

    private static async Task VerifyFreshPortableStartupAsync(string root)
    {
        var store = new PortableStore(root);
        store.Initialize();
        var state = store.Load();
        string expectedDefaults = JsonSerializer.Serialize(new AppState());
        Check(JsonSerializer.Serialize(state) == expectedDefaults && state.Friends.Count == 1
            && state.Friends[0].Name == DefaultCharacters.KotoriName && state.Settings.InterfaceTheme == "standard"
            && !Directory.EnumerateFiles(store.AssetsDirectory, "*", SearchOption.AllDirectories).Any(),
            "an empty portable directory starts with only default Kotori and standard theme, without imported assets or user configuration");
        var window = new MainWindow(store, state, enableInitiative: false, enableMemoryLearning: false)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var character = state.Friends.Single();
            Check(character.Messages.Count == 0 && character.Memories.Count == 0
                && string.IsNullOrEmpty(state.Settings.ProtectedApiKey) && string.IsNullOrEmpty(state.Settings.Model)
                && string.IsNullOrEmpty(character.Voice.ProtectedApiKey) && string.IsNullOrEmpty(character.Voice.GptWeights)
                && string.IsNullOrEmpty(character.Voice.SovitsWeights) && string.IsNullOrEmpty(character.Voice.ReferenceAudio)
                && string.IsNullOrEmpty(character.ChatBackgroundPath) && string.IsNullOrEmpty(character.AvatarPath)
                && string.IsNullOrEmpty(character.PortraitPath)
                && ((ComboBoxItem)((ComboBox)window.FindName("InterfaceThemeBox")).SelectedItem).Tag as string == "standard"
                && ((StackPanel)window.FindName("MessagesPanel")).Children.Count == 1,
                "first-run WPF shows only the default greeting and contains no saved chats, memories, keys, voice weights, backgrounds or avatars");
        }
        finally { window.Close(); }
        Check(JsonSerializer.Serialize(store.Load()) == expectedDefaults,
            "closing and reopening the first-run store preserves a clean default profile without manufacturing chat or memory records");
    }
}
