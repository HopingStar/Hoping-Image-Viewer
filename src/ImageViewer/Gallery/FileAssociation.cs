using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ImageViewer.Gallery;

/// <summary>
/// 文件关联：把图片扩展名关联到本程序，双击 / 打开方式即可用本软件打开。
/// 写入当前用户注册表 HKCU（无需管理员）：
///  1) HKCU\Software\Classes 的 ProgId + OpenWithProgIds + 默认值（打开方式候选 + 无 UserChoice 时的默认）
///  2) HKCU\...\Explorer\FileExts\.ext\UserChoice（Windows 10+ 的"默认程序"记录，优先级最高）
/// UserChoice 需要生成微软的 Hash（基于键 LastWriteTime 的 MD5 + 字节扰乱），见 CalculateHash。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileAssociation
{
    private const string ProgId = "HopingImageViewer.1";
    private const string ProgIdDescription = "Hoping Image Viewer 图片";
    private const string ClassesRoot = @"Software\Classes";
    private const string ExplorerFileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    // 系统图片文件图标（Windows 内置 imageres.dll），比用程序 exe 图标更贴合图片文件
    private const string ImageIcon = @"%SystemRoot%\System32\imageres.dll,2";

    /// <summary>按勾选的扩展名应用关联：勾选的建立关联（含 UserChoice 默认），未勾选的解除。全部解除时清理 ProgId。</summary>
    public static void Apply(string exePath, IReadOnlyCollection<string> extensions)
    {
        var want = new HashSet<string>(extensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var anyKept = false;
        foreach (var ext in ImageService.SupportedExtensions)
        {
            if (want.Contains(ext))
            {
                SetAssociation(exePath, ext);
                SetUserChoice(ext);   // 设为 Windows 默认程序（UserChoice + Hash）
                anyKept = true;
            }
            else
            {
                ClearAssociation(ext);
            }
        }

        using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot);
        if (!anyKept)
            classes.DeleteSubKeyTree(ProgId, false);
    }

    /// <summary>该扩展名当前是否已关联到本软件（打开方式候选或 UserChoice 默认）。</summary>
    public static bool IsAssociated(string ext)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot);
            using var openWith = classes?.OpenSubKey(ext + @"\OpenWithProgIds");
            if (openWith?.GetValue(ProgId) is not null) return true;
            // 也可能是 UserChoice 默认指向本软件
            using var extKey = Registry.CurrentUser.OpenSubKey(ExplorerFileExts + "\\" + ext);
            using var userChoice = extKey?.OpenSubKey("UserChoice");
            return string.Equals(userChoice?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------- HKCU\Software\Classes 关联（打开方式 + 回退默认） ----------

    private static void SetAssociation(string exePath, string ext)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot);
            // 扩展名默认值 → ProgId（无 UserChoice 时的回退默认）
            using (var extKey = classes.CreateSubKey(ext))
                extKey.SetValue("", ProgId);
            // 打开方式候选
            using (var openWith = classes.CreateSubKey(ext + @"\OpenWithProgIds"))
                openWith.SetValue(ProgId, "", RegistryValueKind.String);

            // ProgId 定义：显示名 + 系统图片图标 + 打开命令
            using var progId = classes.CreateSubKey(ProgId);
            progId.SetValue("", ProgIdDescription);
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue("", ImageIcon);
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch { }
    }

    private static void ClearAssociation(string ext)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot);
            using (var extKey = classes.OpenSubKey(ext, true))
            {
                if (extKey is not null && string.Equals(extKey.GetValue("") as string, ProgId, StringComparison.OrdinalIgnoreCase))
                    extKey.DeleteValue("", false);
            }
            using var openWith = classes.OpenSubKey(ext + @"\OpenWithProgIds", true);
            openWith?.DeleteValue(ProgId, false);
        }
        catch { }

        // 解除 UserChoice 默认（若指向本软件），让系统回退默认
        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey(ExplorerFileExts + "\\" + ext, true);
            using var userChoice = extKey?.OpenSubKey("UserChoice");
            if (userChoice is not null &&
                string.Equals(userChoice.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase))
                extKey!.DeleteSubKey("UserChoice", false);
        }
        catch { }
    }

    // ---------- UserChoice（Windows 默认程序，优先级最高） ----------

    /// <summary>把扩展名的 Windows「默认程序」设为本软件：写 ProgId → 读键 LastWriteTime → 算 Hash → 写 Hash。
    /// 按 0install（Zero Install）的成熟实现，见 https://github.com/0install/0install-dotnet FileType.Hash.cs。</summary>
    private static void SetUserChoice(string ext)
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid)) return;
            using (var extKey = Registry.CurrentUser.CreateSubKey(ExplorerFileExts + "\\" + ext))
            {
                // UserChoice 有特殊 ACL，须删除重建
                extKey.DeleteSubKey("UserChoice", false);
                using var userChoice = extKey.CreateSubKey("UserChoice");
                userChoice.SetValue("ProgId", ProgId);
                // Hash 必须基于「键的 LastWriteTime」生成；写入 Hash 又会更新 LastWriteTime，
                // 只要在同一分钟内即稳定，故循环到分钟不再变化
                for (int i = 0; i < 5; i++)
                {
                    long ft = GetKeyLastWriteTime(userChoice);
                    userChoice.SetValue("Hash", CalculateHash(ext, ProgId, sid, ft));
                    long ft2 = GetKeyLastWriteTime(userChoice);
                    if (ft / 60_000_000 == ft2 / 60_000_000) break;
                }
            }
        }
        catch { /* 写失败不抛给前端，打开方式关联仍可用 */ }
    }

    private static string CalculateHash(string extension, string progId, string sid, long lastWriteFileTime)
    {
        // 时间戳取到分钟（0install 用 new DateTime(y,m,d,h,min,0).ToFileTime()，等价于 FileTime 分钟对齐）
        string lastWriteString = (lastWriteFileTime - lastWriteFileTime % 60_000_000).ToString("x16");
        const string experience = @"user choice set via windows user experience {d18b6dd5-6124-4341-9318-804003bafa0b}";
        byte[] data = Encoding.Unicode.GetBytes((extension + sid + progId).ToLower() + lastWriteString + experience + "\0");
        using var md5 = MD5.Create();
        byte[] md5Bytes = md5.ComputeHash(data);
        byte[] p1 = HashInnerPart1(data, md5Bytes);
        byte[] p2 = HashInnerPart2(data, md5Bytes);
        byte[] result = new byte[8];
        for (int i = 0; i < 8; i++) result[i] = (byte)(p1[i] ^ p2[i]);
        return Convert.ToBase64String(result);
    }

    // 以下两个 HashInnerPart 反编译自 Windows 的 UserChoice Hash 算法（0install 实现，逻辑等价 Firefox 的 HashString）

    private static byte[] HashInnerPart1(byte[] data, byte[] hashedData)
    {
        byte[] result = new byte[8];

        uint length = (uint)((((data.Length >> 2) & 1) < 1 ? 1 : 0) + (data.Length >> 2) - 1);
        uint[] dword_data = new uint[length];
        uint[] dword_md5 = new uint[4];
        for (int i = 0; i < dword_data.Length; i++)
            dword_data[i] = BitConverter.ToUInt32(data, i * 4);
        dword_md5[0] = BitConverter.ToUInt32(hashedData, 0);
        dword_md5[1] = BitConverter.ToUInt32(hashedData, 4);
        dword_md5[2] = BitConverter.ToUInt32(hashedData, 8);
        dword_md5[3] = BitConverter.ToUInt32(hashedData, 12);

        if (length <= 1 || (length & 1) == 1)
            return result;

        uint v5 = 0, v6 = 0;
        uint v7 = (length - 2) >> 1;
        uint v18 = v7++;
        uint v8 = v7;
        uint v19 = v7;
        uint res = 0;
        uint v9 = (dword_md5[1] | 1) + 0x13DB0000u;
        uint v10 = (dword_md5[0] | 1) + 0x69FB0000u;

        do
        {
            uint v11 = dword_data[v6] + res;
            v6 += 2;
            uint v12 = 0x79F8A395u * (v10 * v11 - 0x10FA9605u * (v11 >> 16)) + 0x689B6B9Fu * ((v10 * v11 - 0x10FA9605u * (v11 >> 16)) >> 16);
            uint v13 = 0xEA970001u * v12 - 0x3C101569u * (v12 >> 16);
            uint v14 = v13 + v5;
            uint v15 = v9 * (dword_data[v6 - 1] + v13) - 0x3CE8EC25u * ((dword_data[v6 - 1] + v13) >> 16);
            res = 0x1EC90001u * (0x59C3AF2Du * v15 - 0x2232E0F1u * (v15 >> 16)) + 0x35BD1EC9u * ((0x59C3AF2Du * v15 - 0x2232E0F1u * (v15 >> 16)) >> 16);
            v5 = res + v14;
            --v8;
        } while (v8 != 0);
        if (length - 2 - 2 * v18 == 1)
        {
            uint v16 = (dword_data[2 * v19] + res) * v10 - 0x10FA9605u * ((dword_data[2 * v19] + res) >> 16);
            uint v17 = 0x39646B9Fu * (v16 >> 16) + 0x28DBA395u * v16 - 0x3C101569u * ((0x689B6B9Fu * (v16 >> 16) + 0x79F8A395u * v16) >> 16);
            res = 0x35BD1EC9u * ((0x59C3AF2Du * (v17 * v9 - 0x3CE8EC25u * (v17 >> 16)) - 0x2232E0F1u * ((v17 * v9 - 0x3CE8EC25u * (v17 >> 16)) >> 16)) >> 16) + 0x2A18AF2Du * (v17 * v9 - 0x3CE8EC25u * (v17 >> 16)) - 0xFD6BE0F1u * ((v17 * v9 - 0x3CE8EC25u * (v17 >> 16)) >> 16);
            v5 += res + v17;
        }

        BitConverter.GetBytes(res).CopyTo(result, 0);
        BitConverter.GetBytes(v5).CopyTo(result, 4);
        return result;
    }

    private static byte[] HashInnerPart2(byte[] data, byte[] hashedData)
    {
        byte[] result = new byte[8];

        uint length = (uint)((((data.Length >> 2) & 1) < 1 ? 1 : 0) + (data.Length >> 2) - 1);
        uint[] dword_data = new uint[length];
        uint[] dword_md5 = new uint[4];
        for (int i = 0; i < dword_data.Length; i++)
            dword_data[i] = BitConverter.ToUInt32(data, i * 4);
        dword_md5[0] = BitConverter.ToUInt32(hashedData, 0);
        dword_md5[1] = BitConverter.ToUInt32(hashedData, 4);
        dword_md5[2] = BitConverter.ToUInt32(hashedData, 8);
        dword_md5[3] = BitConverter.ToUInt32(hashedData, 12);

        if (length <= 1 || (length & 1) == 1)
            return result;

        uint v5 = 0, v6 = 0, v7 = 0;
        uint v25 = (length - 2) >> 1;
        uint v21 = dword_md5[0] | 1;
        uint v22 = dword_md5[1] | 1;
        uint v23 = 0xB1110000u * v21;
        uint v24 = 0x16F50000u * v22;
        uint v8 = v25 + 1;

        do
        {
            v6 += 2;
            uint v9 = (dword_data[v6 - 2] + v5) * v23 - 0x30674EEFu * (v21 * (dword_data[v6 - 2] + v5) >> 16);
            uint v10 = v9 >> 16;
            uint v11 = 0xE9B30000u * v10 + 0x12CEB96Du * ((0x5B9F0000u * v9 - 0x78F7A461u * v10) >> 16);
            uint v12 = 0x1D830000u * v11 + 0x257E1D83u * (v11 >> 16);
            uint v13 = ((v12 + dword_data[v6 - 1]) * v24 - 0x5D8BE90Bu * ((v22 * (v12 + dword_data[v6 - 1])) >> 16)) >> 16;
            uint v14 = 0x96FF0000u * ((v12 + dword_data[v6 - 1]) * v24 - 0x5D8BE90Bu * ((v22 * (v12 + dword_data[v6 - 1])) >> 16)) - 0x2C7C6901u * v13 >> 16;
            v5 = 0xF2310000u * v14 - 0x405B6097u * ((0x7C932B89u * v14 - 0x5C890000u * v13) >> 16);
            v7 += v5 + v12;
            --v8;
        } while (v8 != 0);
        if (length - 2 - 2 * v25 == 1)
        {
            uint v15 = 0xB1110000u * v21 * (dword_data[2 * (v25 + 1)] + v5) - 0x30674EEFu * (v21 * (dword_data[2 * (v25 + 1)] + v5) >> 16);
            uint v16 = v15 >> 16;
            uint v17 = (0x5B9F0000u * v15 - 0x78F7A461u * (v15 >> 16)) >> 16;
            uint v18 = 0x257E1D83u * ((0xE9B30000u * v16 + 0x12CEB96Du * v17) >> 16) + 0x3BC70000u * v17;
            uint v19 = (0x16F50000u * v18 * v22 - 0x5D8BE90Bu * (v18 * v22 >> 16)) >> 16;
            uint v20 = (0x96FF0000u * (0x16F50000u * v18 * v22 - 0x5D8BE90Bu * (v18 * v22 >> 16)) - 0x2C7C6901u * v19) >> 16;
            v5 = 0xF2310000u * v20 - 0x405B6097u * ((0x7C932B89u * v20 - 0x5C890000u * v19) >> 16);
            v7 += v5 + v18;
        }

        BitConverter.GetBytes(v5).CopyTo(result, 0);
        BitConverter.GetBytes(v7).CopyTo(result, 4);

        return result;
    }

    // ---------- 读取注册表键 LastWriteTime ----------

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegQueryInfoKey(
        SafeRegistryHandle hKey,
        IntPtr lpClass, IntPtr lpcClass, IntPtr lpReserved,
        out uint lpcSubKeys, out uint lpcMaxSubKeyLen, out uint lpcMaxClassLen,
        out uint lpcValues, out uint lpcMaxValueNameLen, out uint lpcMaxValueLen,
        out uint lpcSecurityDescriptor, out long lpftLastWriteTime);

    private static long GetKeyLastWriteTime(RegistryKey key)
    {
        if (RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            out _, out _, out _, out _, out _, out _, out _, out long ft) == 0)
            return ft;
        return 0;
    }
}
