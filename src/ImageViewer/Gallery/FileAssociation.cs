using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ImageViewer.Gallery;

/// <summary>
/// 文件关联：把图片扩展名关联到本程序，双击 / 打开方式即可用本软件打开。
/// 写入当前用户注册表 HKCU\Software\Classes（无需管理员），通过自定义 ProgId 实现。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileAssociation
{
    private const string ProgId = "HopingImageViewer.1";
    private const string ProgIdDescription = "Hoping Image Viewer 图片";
    private const string ClassesRoot = @"Software\Classes";

    /// <summary>按勾选的扩展名应用关联：勾选的建立关联，未勾选的解除（保持与设置一致）。全部解除时清理 ProgId。</summary>
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

        // 全部解除关联：删除 ProgId 定义（不留空壳）
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot);
        if (!anyKept)
            classes.DeleteSubKeyTree(ProgId, false);
    }

    /// <summary>该扩展名当前是否已关联到本软件。</summary>
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
            // 扩展名 → 打开方式列表里登记本 ProgId
            using (var openWith = classes.CreateSubKey(ext + @"\OpenWithProgIds"))
                openWith.SetValue(ProgId, "", RegistryValueKind.String);

            // ProgId 定义：显示名 + 图标 + 打开命令
            using var progId = classes.CreateSubKey(ProgId);
            progId.SetValue("", ProgIdDescription);
            using (var icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{exePath}\",0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exePath}\" \"%1\"");
        }
        catch { /* 注册表写入失败（如权限）不抛给前端，设置页会再次尝试 */ }
    }

    private static void ClearAssociation(string ext)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ClassesRoot);
            using var openWith = classes.OpenSubKey(ext + @"\OpenWithProgIds", true);
            openWith?.DeleteValue(ProgId, false);
        }
        catch { }
    }
}
