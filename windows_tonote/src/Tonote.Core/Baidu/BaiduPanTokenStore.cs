using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Tonote.Core;

/// <summary>
/// 令牌存在数据目录的 baidupan-token.json。
/// W-37：Windows 上用 DPAPI（CurrentUser）加密落盘，只有当前 Windows 用户能解密；
/// 兼容已存在的明文文件（读到后下次保存自动升级为密文）。非 Windows 上仅限制文件权限。
/// </summary>
public sealed class BaiduPanTokenStore
{
    public const string FileName = "baidupan-token.json";

    public BaiduPanTokenStore(string root) => Root = root;

    public string Root { get; }

    public string Path => System.IO.Path.Combine(Root, FileName);

    public BaiduPanToken? Load()
    {
        if (!File.Exists(Path))
            return null;
        try
        {
            var raw = File.ReadAllBytes(Path);
            string json;
            if (OperatingSystem.IsWindows())
            {
                try { json = Encoding.UTF8.GetString(Unprotect(raw)); }
                catch { json = Encoding.UTF8.GetString(raw); } // 旧明文文件兼容
            }
            else json = Encoding.UTF8.GetString(raw);

            var token = TonoteJson.Deserialize<BaiduPanToken>(json);
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
                return null;
            return token;
        }
        catch
        {
            return null;
        }
    }

    public void Save(BaiduPanToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(TonoteJson.Serialize(token));
        if (OperatingSystem.IsWindows())
            bytes = Protect(bytes);
        TonoteJson.WriteAtomicBytes(Path, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }

    // ── DPAPI（CurrentUser）─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private static DataBlob Alloc(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DataBlob { cbData = data.Length, pbData = pointer };
    }

    private static void Free(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
    }

    private static byte[] Protect(byte[] plain)
    {
        var input = Alloc(plain);
        var output = default(DataBlob);
        try
        {
            if (!CryptProtectData(ref input, "TonoteToken", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, out output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Free(input);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        var input = Alloc(cipher);
        var output = default(DataBlob);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0, out output))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            Free(input);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
