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

    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (Gate) { if (Cache.TryGetValue(path, out var cached)) return cached; }

        ImageSource? result = null;
        var ok = false;
        var info = new Native.SHFILEINFO();
        uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
        uint attrs = 0;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            flags |= Native.SHGFI_USEFILEATTRIBUTES;
            attrs = Native.FILE_ATTRIBUTE_NORMAL;
        }
        try
        {
            var r = Native.SHGetFileInfo(path, attrs, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.SHFILEINFO>(), flags);
            if (info.hIcon != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                result = src;
                Native.DestroyIcon(info.hIcon);
            }
            ok = true;
        }
        catch (Exception ex) { Log.Ex("取图标失败: " + path, ex); }

        // 失败不写缓存，留给 UI 线程上懒加载再试一次
        if (ok) lock (Gate) { Cache[path] = result; }
        return result;
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
