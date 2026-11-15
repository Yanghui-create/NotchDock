using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchDock;

/// <summary>取文件 / 文件夹 / 快捷方式的系统图标，带缓存。</summary>
public static class IconService
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var key = path;
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? result = null;
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
        }
        catch (Exception ex) { Log.Ex("取图标失败: " + path, ex); }

        Cache[key] = result;
        return result;
    }

    public static void Invalidate(string path) => Cache.Remove(path);
}
