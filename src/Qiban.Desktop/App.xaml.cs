using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Qiban.Core;

namespace Qiban.Desktop;

public partial class App : Application { }

internal static class Boot
{
    [STAThread]
    public static void Main()
    {
        PortableStore? store = null;
        bool smokeTest = Environment.GetCommandLineArgs().Contains("--smoke-test");
        try
        {
            var root = AppContext.BaseDirectory;
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..20];
            using var mutex = new Mutex(true, "Local\\Qiban-" + id, out var first);
            if (!first) { MessageBox.Show("此目录中的致电已在运行。", "致电"); return; }
            store = new PortableStore(root);
            store.Initialize();
            var state = store.Load();
            if (DefaultCharacters.UpgradeLegacyDefault(state)) store.Save(state);
            // The per-directory mutex guarantees that no app-owned download or player is active.
            store.CleanupCache(state.Settings, true);
            store.PruneAssets(state);
            var app = new App();
            app.InitializeComponent();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // Do not log chat text, credentials or provider response bodies.
                MessageBox.Show("操作未完成，请重试。\n" + SafeError(e.Exception), "致电", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            var window = new MainWindow(store, state, enableInitiative: !smokeTest, enableMemoryLearning: !smokeTest);
            if (smokeTest)
            {
                window.ShowActivated = false;
                window.Loaded += (_, _) =>
                {
                    File.WriteAllText(Path.Combine(store.DataDirectory, "startup-check.txt"), "PASS: portable executable initialized WPF and storage.");
                    window.Dispatcher.BeginInvoke(window.Close);
                };
            }
            app.Run(window);
            store.CleanupCache(state.Settings, true);
        }
        catch (Exception e)
        {
            if (smokeTest && store != null)
            {
                File.WriteAllText(Path.Combine(store.DataDirectory, "startup-check.txt"), "FAIL: " + e.GetType().Name + " / " + SafeError(e));
                Environment.ExitCode = 1;
                return;
            }
            MessageBox.Show("致电无法启动。请将完整程序文件夹放在有写入权限的目录（例如 D 盘），不要直接在压缩包内运行。\n\n" + SafeError(e), "致电", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static string SafeError(Exception e) => e switch
    {
        HttpRequestException => "连接失败，请检查服务地址、网络和服务状态。",
        OperationCanceledException => "请求已取消或超时。",
        UnauthorizedAccessException => "没有目录写入权限，或目录指向程序外部。",
        IOException when e.Source == "Qiban.Core" => e.Message.Length > 300 ? e.Message[..300] : e.Message,
        IOException => "文件读写失败。检查磁盘空间、文件占用及备份。",
        _ => e.Message.Length > 300 ? e.Message[..300] : e.Message
    };
}
