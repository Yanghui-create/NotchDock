using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchDock;

/// <summary>取文件 / 文件夹 / 快捷方式的系统图标，带缓存。</summary>
public static class IconService
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private static ImageSource? _folderFallback;
    private static ImageSource? _fileFallback;

    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (Gate) { if (Cache.TryGetValue(path, out var cached)) return cached; }

        bool isDir = Directory.Exists(path);

        // 文件夹优先读 desktop.ini 的 IconResource——这是用户"自定义过的图标"，
        // SHGetFileInfo 对带 desktop.ini 的文件夹时好时坏，直接 ExtractIconEx 最可靠。
        ImageSource? result = isDir ? GetCustomFolderIcon(path) : null;

        // 自定义图标没有 → 退回 SHGetFileInfo（普通文件夹/文件/快捷方式）
        if (result == null)
        {
            var info = new Native.SHFILEINFO();
            uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
            uint attrs = 0;
            bool exists = isDir || File.Exists(path);
            if (!exists)
            {
                flags |= Native.SHGFI_USEFILEATTRIBUTES;
                attrs = Native.FILE_ATTRIBUTE_NORMAL;
            }
            try
            {
                Native.SHGetFileInfo(path, attrs, ref info,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), flags);
                if (info.hIcon != IntPtr.Zero)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    result = src;
                    Native.DestroyIcon(info.hIcon);
                }
            }
            catch (Exception ex) { Log.Ex("取图标失败: " + path, ex); }
        }

        // 只缓存"真实取到"的图标。兜底默认图标绝不写缓存——
        // 否则后台预热取失败→缓存默认图标→真实自定义图标再也进不来（曾导致用户自定义图标变默认）。
        if (result != null) lock (Gate) { Cache[path] = result; }
        return result ?? Fallback(isDir);
    }

    /// <summary>
    /// 读取文件夹 desktop.ini 的 IconResource=DLL,索引 或 IconFile=xxx.ico，提取自定义图标。
    /// 完全是文件读取 + ExtractIconEx，不依赖 Shell 状态，后台线程也稳定。
    /// </summary>
    private static ImageSource? GetCustomFolderIcon(string dir)
    {
        string ini = System.IO.Path.Combine(dir, "desktop.ini");
        if (!File.Exists(ini)) return null;
        try
        {
            foreach (var raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                string? spec = null;
                int index = 0;
                if (line.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                {
                    spec = line.Substring("IconResource=".Length).Trim();
                    int comma = spec.LastIndexOf(',');
                    if (comma < 0) continue;
                    if (!int.TryParse(spec.Substring(comma + 1).Trim(), out index)) continue;
                    spec = spec.Substring(0, comma).Trim().Trim('"');
                }
                else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    spec = line.Substring("IconFile=".Length).Trim().Trim('"');
                    index = 0;
                }
                if (spec == null) continue;

                // 相对路径按文件夹所在目录解析；环境变量（如 %SystemRoot%）展开
                if (!System.IO.Path.IsPathRooted(spec))
                    spec = Environment.ExpandEnvironmentVariables(spec);
                spec = Environment.ExpandEnvironmentVariables(spec);
                if (!System.IO.Path.IsPathRooted(spec))
                    spec = System.IO.Path.Combine(dir, spec);
                if (!File.Exists(spec)) continue;

                var icon = ExtractIcon(spec, index);
                if (icon != null) return icon;
            }
        }
        catch (Exception ex) { Log.Ex("读 desktop.ini 图标失败: " + dir, ex); }
        return null;
    }

    private static ImageSource? ExtractIcon(string file, int index)
    {
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            uint n = Native.ExtractIconEx(file, index, out large, out small, 1);
            if (n > 0 && large != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
        }
        catch { }
        finally
        {
            if (large != IntPtr.Zero) Native.DestroyIcon(large);
            if (small != IntPtr.Zero) Native.DestroyIcon(small);
        }
        return null;
    }

    /// <summary>系统默认的文件夹/文件图标（SHGetFileInfo + USEFILEATTRIBUTES，几乎不会失败）</summary>
    private static ImageSource? Fallback(bool isDir)
    {
        lock (Gate)
        {
            var hit = isDir ? _folderFallback : _fileFallback;
            if (hit != null) return hit;
        }

        ImageSource? src = null;
        try
        {
            var info = new Native.SHFILEINFO();
            uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON | Native.SHGFI_USEFILEATTRIBUTES;
            uint attrs = isDir ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
            Native.SHGetFileInfo(isDir ? "folder" : "file.txt", attrs, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), flags);
            if (info.hIcon != IntPtr.Zero)
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                src = bmp;
                Native.DestroyIcon(info.hIcon);
            }
        }
        catch { }
        lock (Gate)
        {
            if (isDir) _folderFallback = src; else _fileFallback = src;
        }
        return src;
    }

    /// <summary>
    /// 后台预热：提前把一批路径的图标取好缓存起来。
    /// SHGetFileInfo + CreateBitmapSourceFromHIcon 都是可冻结的自由线程对象，
    /// 放在后台线程跑不会碰 UI；万一哪个路径失败，只是不进缓存，不影响使用。
    /// </summary>
    public static void Prewarm(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { Get(p); } catch { }
        }
    }

    public static void Invalidate(string path)
    {
        lock (Gate) { Cache.Remove(path); }
    }
}
