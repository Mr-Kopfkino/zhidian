using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Qiban.Core;

internal static class CacheCleanupTests
{
    internal static void Run(string testRoot, Action<bool, string> check)
    {
        var store = new PortableStore(Path.Combine(testRoot, "cache-directory-cleanup"));
        store.Initialize();
        var settings = new AppSettings();
        string sessionRoot = Path.Combine(store.CacheDirectory, "voice-engine");
        string crashed = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(crashed, "hf_home", "hub", "empty"));
        Directory.CreateDirectory(Path.Combine(crashed, "temp", "download"));
        File.WriteAllText(Path.Combine(crashed, "partial.wav.part"), "crash");
        string data = Path.Combine(store.DataDirectory, "keep.txt");
        File.WriteAllText(data, "permanent");
        var restarted = new PortableStore(store.Root);
        restarted.Initialize();
        long freed = restarted.CleanupCache(settings, all: true);
        check(freed == 5 && !Directory.Exists(crashed), "restart full cache cleanup removes crashed audio and the entire empty session tree");
        check(Directory.Exists(store.CacheDirectory) && Directory.Exists(Path.Combine(store.CacheDirectory, "tmp"))
            && Directory.Exists(Path.Combine(store.CacheDirectory, "logs")) && File.ReadAllText(data) == "permanent",
            "cache pruning preserves the cache root process temp/log roots and permanent data");

        string empty = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        string expired = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        string active = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(empty, "profile", "local"));
        Directory.CreateDirectory(Path.Combine(expired, "temp", "empty"));
        Directory.CreateDirectory(Path.Combine(active, "hf_home", "empty"));
        string expiredFile = Path.Combine(expired, "old.wav"), activeFile = Path.Combine(active, "current.part");
        File.WriteAllText(expiredFile, "expired");
        File.SetLastWriteTimeUtc(expiredFile, DateTime.UtcNow.AddDays(-3));
        File.WriteAllText(activeFile, "in-flight");
        string ordinary = Path.Combine(store.CacheDirectory, "download", "nested", "empty");
        Directory.CreateDirectory(ordinary);
        store.CleanupCache(settings);
        check(!Directory.Exists(empty) && !Directory.Exists(expired) && !Directory.Exists(Path.Combine(store.CacheDirectory, "download")),
            "routine cleanup removes empty crash sessions expired sessions and nested empty cache directories");
        check(File.ReadAllText(activeFile) == "in-flight" && Directory.Exists(Path.Combine(active, "hf_home", "empty")),
            "routine cleanup preserves the complete session tree when an in-flight file remains");
        if (OperatingSystem.IsWindows())
        {
            using (var locked = new FileStream(activeFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                store.CleanupCache(settings, all: true);
                check(File.Exists(activeFile) && Directory.Exists(Path.Combine(active, "hf_home", "empty")),
                    "full cleanup leaves a locked running session and its empty environment folders intact");
            }
        }
        store.CleanupCache(settings, all: true);
        check(!Directory.Exists(active), "session directories are pruned after the last retained file can be deleted");
        check(store.CleanupCache(settings, all: true) == 0 && Directory.Exists(store.CacheDirectory),
            "repeated empty cache cleanup is idempotent");

        string outside = Path.Combine(testRoot, "cache-prune-outside");
        string outsideEmpty = Path.Combine(outside, "keep-empty", "nested");
        Directory.CreateDirectory(outsideEmpty);
        string sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "outside");
        string holder = Path.Combine(store.CacheDirectory, "link-holder");
        Directory.CreateDirectory(holder);
        string link = Path.Combine(holder, "outside-link");
        CreateDirectoryLink(link, outside);
        try
        {
            store.CleanupCache(settings, all: true);
            check(File.ReadAllText(sentinel) == "outside" && Directory.Exists(outsideEmpty),
                "full cache pruning never follows a reparse path to delete outside files or empty directories");
            store.CleanupCache(settings);
            check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0 && Directory.Exists(holder)
                && Directory.Exists(outsideEmpty), "routine cache pruning skips the reparse entry and keeps its nonempty parent");
        }
        finally
        {
            // Unlink only the exact directory link created inside this test's cache.
            if (Path.GetFullPath(link).StartsWith(store.CacheDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(link, recursive: false);
        }
        store.CleanupCache(settings);
        check(!Directory.Exists(holder) && Directory.Exists(outsideEmpty), "removing a link allows its now-empty cache parent to be pruned safely");
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return; }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { }
        catch (IOException) when (OperatingSystem.IsWindows()) { }
        // NTFS junctions provide the same no-follow boundary test without the
        // administrator/developer-mode privilege needed for Windows symlinks.
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substitute.Length + print.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint control, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
