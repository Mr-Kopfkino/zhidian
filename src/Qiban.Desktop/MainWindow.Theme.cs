using System.Windows;
using System.Windows.Controls;

namespace Qiban.Desktop;

public partial class MainWindow
{
    private ResourceDictionary? _themeOverrides;
    private bool _loadingTheme;

    private void LoadInterfaceTheme()
    {
        _loadingTheme = true;
        try
        {
            InterfaceThemeBox.SelectedIndex = _state.Settings.InterfaceTheme == "xp" ? 1 : 0;
            ApplyInterfaceTheme();
        }
        finally { _loadingTheme = false; }
    }

    private void OnInterfaceThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loadingTheme || InterfaceThemeBox.SelectedItem is not ComboBoxItem item) return;
        string selected = item.Tag as string == "xp" ? "xp" : "standard";
        if (selected == _state.Settings.InterfaceTheme) return;
        string previous = _state.Settings.InterfaceTheme;
        try
        {
            // Save only this preference. Do not submit other settings drafts or rebuild
            // messages: active streams, selection, scroll position and timers stay intact.
            _state.Settings.InterfaceTheme = selected;
            Save();
            ApplyInterfaceTheme();
        }
        catch (Exception error)
        {
            _state.Settings.InterfaceTheme = previous;
            LoadInterfaceTheme();
            Notice(Boot.SafeError(error));
        }
    }

    private void ApplyInterfaceTheme()
    {
        bool xp = _state.Settings.InterfaceTheme == "xp";
        if (xp && _themeOverrides == null)
        {
            _themeOverrides = new ResourceDictionary
            {
                Source = new Uri("/Zhidian;component/Themes/WindowsXp.xaml", UriKind.Relative)
            };
            Resources.MergedDictionaries.Add(_themeOverrides);
        }
        else if (!xp && _themeOverrides != null)
        {
            Resources.MergedDictionaries.Remove(_themeOverrides);
            _themeOverrides = null;
        }
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CaptionHeight = xp ? 32 : 42;
        ApplyNativeFrameStyle();
    }
}
