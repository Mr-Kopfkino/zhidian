using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Qiban.Core;

/// <summary>Windows user-bound DPAPI encryption. Ciphertext can be kept beside portable user data.</summary>
public static class SecretVault
{
    private const string Prefix = "dpapi:";

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var clear = Encoding.UTF8.GetBytes(secret);
        try { return Prefix + Convert.ToBase64String(Transform(clear, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        if (!encrypted.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("密钥存储格式不受支持，请重新输入密钥。");
        try
        {
            var clear = Transform(Convert.FromBase64String(encrypted[Prefix.Length..]), protect: false);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new CryptographicException("无法解密保存的密钥。密钥与 Windows 用户绑定；更换设备或用户后请重新填写。", error);
        }
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥加密需要 Windows DPAPI。");
        var input = new DataBlob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, output.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
