using System.Diagnostics;

namespace NotchDock;

/// <summary>扫描开始菜单，索引本机可启动的应用。</summary>
public static class AppIndexer
{
    private static List<FavoriteItem>? _cache;

    private static readonly string[] SkipWords =
    {
        "uninstall", "卸载", "readme", "说明", "帮助", "help", "文档", "documentation",
        "website", "网站", "官网", "update", "更新", "license", "许可", "changelog", "release notes",
        "repair", "修复", "命令提示符", "windows 工具", "accessories", "辅助工具",
        "手册", "日志", "manual", "配置工具"
    };

    public static List<FavoriteItem> GetInstalledApps(bool force = false)
    {
        if (_cache != null && !force) return _cache;

        var found = new Dictionary<string, FavoriteItem>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in EnumerateLinks(root))
            {
                var name = Path.GetFileNameWithoutExtension(lnk).Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var lower = name.ToLowerInvariant();
                if (SkipWords.Any(w => lower.Contains(w))) continue;
                if (found.ContainsKey(name)) continue;
                found[name] = new FavoriteItem { Name = name, Target = lnk, Kind = FavKind.App };
            }
        }

        _cache = found.Values.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
        return _cache;
    }

    /// <summary>递归枚举快捷方式，单个目录无权限也不影响整体</summary>
    private static IEnumerable<string> EnumerateLinks(string dir, int depth = 0)
    {
        if (depth > 6) yield break;

        string[] files;
        try { files = Directory.GetFiles(dir, "*.lnk"); }
        catch { files = Array.Empty<string>(); }
        foreach (var f in files) yield return f;

        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { subs = Array.Empty<string>(); }
        foreach (var s in subs)
            foreach (var f in EnumerateLinks(s, depth + 1)) yield return f;
    }

    public static void ClearCache() => _cache = null;
}

public static class Launcher
{
    public static void Open(FavoriteItem item) => OpenPath(item.Target, item.Kind == FavKind.Folder);

    public static void OpenPath(string path, bool isFolder = false)
    {
        try
        {
            if (isFolder || Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }
            if (!File.Exists(path))
            {
                Log.Write($"目标不存在: {path}");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Ex("打开失败: " + path, ex); }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Ex("定位失败", ex); }
    }
}
