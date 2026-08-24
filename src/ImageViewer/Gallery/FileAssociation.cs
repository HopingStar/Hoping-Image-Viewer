using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ImageViewer.Gallery;

/// <summary>
/// 文件关联：把图片扩展名关联到本软件，写入当前用户注册表 HKCU（无需管理员）。
///  1) HKCU\Software\Classes 的 ProgId + OpenWithProgIds + 默认值（「打开方式」候选 / 回退默认）
///  2) 关联后引导用户通过系统「打开方式 → 始终使用」设置双击默认（一次手动，Windows 官方唯一可靠途径）
///
/// 说明：Windows 10 22H2 起（2024+）微软用 UCPD 驱动 + 更新的 UserChoice Hash 算法阻止第三方
/// 程序自动写入「默认程序」，旧版 UserChoice 算法（2023 前逆向的）已失效且写入会被回滚。
/// 因此本软件不再尝试写 UserChoice，改为可靠的打开方式关联 + 引导。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileAssociation
{
    private const string ProgId = "HopingImageViewer.1";
    private const string ProgIdDescription = "Hoping Image Viewer 图片";
    private const string ClassesRoot = @"Software\Classes";
    // 系统图片文件图标（Windows 内置 imageres.dll），比用程序 exe 图标更贴合图片文件
    private const string ImageIcon = @"%SystemRoot%\System32\imageres.dll,2";

    /// <summary>按勾选的扩展名应用关联：勾选的建立「打开方式」关联，未勾选的解除。全部解除时清理 ProgId。</summary>
    public static void Apply(string exePath, IReadOnlyCollection<string> extensions)
    {
        var want = new HashSet<string>(extensions ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var anyKept = false;
        foreach (var ext in ImageService.SupportedExtensions)
        {
            if (want.Contains(ext))
            {
                SetAssociation(exePath, ext);
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

        // 通知资源管理器刷新文件关联与图标（否则 explorer 缓存旧的打开方式）
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>该扩展名当前是否已关联到本软件（打开方式候选）。</summary>
    public static bool IsAssociated(string ext)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot);
            using var openWith = classes?.OpenSubKey(ext + @"\OpenWithProgIds");
            return openWith?.GetValue(ProgId) is not null;
        }
        catch { return false; }
    }

    private static void SetAssociation(string exePath, string ext)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot);
            // 扩展名默认值 → ProgId（作为「打开方式」候选的回退默认）
            using (var extKey = classes.CreateSubKey(ext))
                extKey.SetValue("", ProgId);
            // 打开方式候选（右键「打开方式」里出现本软件）
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
    }
}
