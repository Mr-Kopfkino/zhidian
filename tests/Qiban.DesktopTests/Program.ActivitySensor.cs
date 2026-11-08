using System.IO;
using System.Reflection;
using Qiban.Core;
using Qiban.Desktop;

namespace Qiban.DesktopTests;

internal static partial class Program
{
    private static void VerifyActivitySensor()
    {
        var sensor = typeof(DesktopActivityMonitor);
        var describe = sensor.GetMethod("DescribeForegroundProcesses", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, IEnumerable<string?>, ActivityObservation?>>();
        var readName = sensor.GetMethod("ReadProcessName", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<uint, string?>>();

        Check(describe("ApplicationFrameHost", ["ApplicationFrameHost", "Video.UI"]) ==
            new ActivityObservation("Video.UI", "视频播放器"),
            "a Windows hosted player resolves to the content process instead of dropping its foreground frame");
        Check(describe("APPLICATIONFRAMEHOST.exe", [null, "ApplicationFrameHost", "CHROME.exe", "chrome"]) ==
            new ActivityObservation("CHROME", "浏览器"),
            "hosted browser resolution ignores unavailable names and deduplicates the same executable");
        Check(describe("ApplicationFrameHost", ["Music.UI"])?.Category == "音乐播放器"
            && describe("ApplicationFrameHost", ["HostedFixture"]) == new ActivityObservation("HostedFixture", "应用"),
            "hosted music and unknown apps retain their actual executable category");
        Check(describe("ApplicationFrameHost", []) is null
            && describe("ApplicationFrameHost", ["ApplicationFrameHost", "explorer", "Zhidian", "Laidian", "LockApp"]) is null,
            "a frame without eligible content is never itself labeled as a player or activity");
        Check(describe("ApplicationFrameHost", ["Video.UI", "chrome"]) is null,
            "multiple different hosted apps are rejected rather than selecting arbitrary content");
        Check(describe("ApplicationFrameHost", ["C:\\private\\Video.UI.exe", "invalid\nname", null]) is null,
            "hosted resolution cannot introduce paths or invalid process names into observations");
        Check(describe("chrome", UnexpectedChildren()) == new ActivityObservation("chrome", "浏览器")
            && describe("explorer", UnexpectedChildren()) is null,
            "ordinary foreground apps and excluded shell windows never inspect child applications");

        string expectedName = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
        string? actualName = readName((uint)Environment.ProcessId);
        Check(string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase)
            && actualName is not null && !actualName.Contains('\\') && !actualName.Contains('/'),
            "the actual Windows limited-query API resolves only the test executable basename without activating a window");
        Check(readName(0) is null && readName(uint.MaxValue) is null,
            "invalid or unavailable native process handles fail closed without elevation");

        static IEnumerable<string?> UnexpectedChildren() => Enumerable.Range(0, 1).Select<int, string?>(_ =>
            throw new InvalidOperationException("Only an application frame may enumerate hosted content."));
    }
}
