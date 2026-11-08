using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Qiban.Core;

namespace Qiban.Desktop;

/// <summary>Reports only the foreground app's executable name. No titles, paths or background inventory.</summary>
public sealed class DesktopActivityMonitor
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zhidian", "Laidian", "Qiban", "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "SearchUI", "LockApp", "LogonUI", "winlogon", "dwm",
        "ApplicationFrameHost", "TextInputHost", "InputApp", "SystemSettings", "consent",
        "CredentialUIBroker", "ScreenClippingHost", "Taskmgr", "System", "Idle"
    };
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "waterfox", "librewolf",
        "floorp", "zen", "browser", "360chrome", "360se", "QQBrowser", "sogouexplorer"
    };
    private static readonly HashSet<string> VideoPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "PotPlayerMini",
        "PotPlayerMini64", "wmplayer", "MediaPlayer", "Video.UI", "KMPlayer", "QQPlayer"
    };
    private static readonly HashSet<string> MusicPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spotify", "cloudmusic", "QQMusic", "foobar2000", "Music.UI", "MusicBee", "AIMP"
    };
    private static readonly HashSet<string> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        "GenshinImpact", "YuanShen", "StarRail", "ZenlessZoneZero", "Minecraft.Windows",
        "RobloxPlayerBeta", "League of Legends", "VALORANT-Win64-Shipping", "cs2", "dota2",
        "eldenring", "Cyberpunk2077", "Stardew Valley", "Terraria", "HollowKnight", "ffxiv_dx11"
    };

    /// <summary>
    /// False when this app's desktop is not receiving input (including the secure/lock desktop).
    /// This is a read-only query; it never switches or opens the user's desktop.
    /// </summary>
    public bool IsSessionAvailable
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            // GetThreadDesktop returns a borrowed handle; Windows explicitly says not to close it.
            var desktop = GetThreadDesktop(GetCurrentThreadId());
            return desktop != IntPtr.Zero &&
                GetUserObjectInformation(desktop, 6 /* UOI_IO */, out int receivesInput, sizeof(int), out _) &&
                receivesInput != 0;
        }
    }

    public ActivityObservation? Sample()
    {
        if (!IsSessionAvailable) return null;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindowVisible(window) ||
            GetWindowThreadProcessId(window, out uint processId) == 0 ||
            processId == 0 || processId > int.MaxValue || processId == Environment.ProcessId) return null;
        string? ownerName = ReadProcessName(processId);
        if (ownerName is null) return null;
        var selectedWindow = window;
        uint selectedProcessId = processId;
        ActivityObservation? observation;
        if (IsFrameHost(ownerName))
        {
            // Store apps can put their content in a different process from the foreground frame.
            // Only descend into this frame; never enumerate other top-level windows or processes.
            var children = new List<(IntPtr Window, uint ProcessId, string Name)>();
            var visitedProcesses = new HashSet<uint>();
            int visitedWindows = 0;
            bool truncated = false;
            EnumChildWindows(window, (child, _) =>
            {
                if (++visitedWindows > 256) { truncated = true; return false; }
                if (!IsWindowVisible(child) || GetWindowThreadProcessId(child, out uint childId) == 0 ||
                    childId == 0 || childId == processId || childId == Environment.ProcessId ||
                    !visitedProcesses.Add(childId)) return true;
                if (visitedProcesses.Count > 32) { truncated = true; return false; }
                string? name = ReadProcessName(childId);
                if (name is not null) children.Add((child, childId, name));
                return true;
            }, IntPtr.Zero);
            if (truncated) return null;
            observation = DescribeForegroundProcesses(ownerName, children.Select(child => child.Name));
            if (observation is null) return null;
            var selected = children.First(child =>
                string.Equals(child.Name, observation.ProcessName, StringComparison.OrdinalIgnoreCase));
            selectedWindow = selected.Window;
            selectedProcessId = selected.ProcessId;
        }
        else observation = DescribeProcess(ownerName);

        // Recheck both the frame and content ownership after querying, including reparented windows.
        if (observation is null || GetForegroundWindow() != window || !IsWindowVisible(window) ||
            GetWindowThreadProcessId(window, out uint currentOwner) == 0 || currentOwner != processId ||
            !IsWindowVisible(selectedWindow) ||
            GetWindowThreadProcessId(selectedWindow, out uint currentProcess) == 0 || currentProcess != selectedProcessId ||
            (selectedWindow != window && !IsChild(window, selectedWindow)) || !IsSessionAvailable) return null;
        return observation;
    }

    private static bool IsFrameHost(string name) =>
        string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    private static ActivityObservation? DescribeForegroundProcesses(string ownerName, IEnumerable<string?> hostedNames)
    {
        if (!IsFrameHost(ownerName)) return DescribeProcess(ownerName);
        ActivityObservation? selected = null;
        foreach (string? name in hostedNames)
        {
            var candidate = name is null ? null : DescribeProcess(name);
            if (candidate is null) continue;
            // Ambiguous hosts are skipped instead of inventing which application the user sees.
            if (selected is not null && !string.Equals(selected.ProcessName, candidate.ProcessName,
                StringComparison.OrdinalIgnoreCase)) return null;
            selected ??= candidate;
        }
        return selected;
    }

    private static string? ReadProcessName(uint processId)
    {
        // Limited query rights also work for many processes whose modules cannot be inspected.
        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process.IsInvalid) return null;
        var imageName = new StringBuilder(1024);
        uint length = (uint)imageName.Capacity;
        if (!QueryFullProcessImageName(process, 0, imageName, ref length))
        {
            if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */) return null;
            imageName.EnsureCapacity(32768);
            length = (uint)imageName.Capacity;
            if (!QueryFullProcessImageName(process, 0, imageName, ref length)) return null;
        }
        // The full path is transient; it never enters observations, prompts, logs or persisted state.
        return Path.GetFileNameWithoutExtension(imageName.ToString());
    }

    /// <summary>Categories are hints from executable names, not proof of on-screen content.</summary>
    public static ActivityObservation? DescribeProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || processName.Length > 128 ||
            processName.Any(c => char.IsControl(c) || c is '/' or '\\')) return null;
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        if (string.IsNullOrWhiteSpace(name) || Ignored.Contains(name)) return null;
        string category = Browsers.Contains(name) ? "浏览器" : VideoPlayers.Contains(name) ? "视频播放器" :
            MusicPlayers.Contains(name) ? "音乐播放器" : Games.Contains(name) ||
            name.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ? "游戏" : "应用";
        return new(name, category);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder imageName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index,
        out int information, uint length, out uint lengthNeeded);
}
