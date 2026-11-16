using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchDock;

/// <summary>
/// 全屏应用（游戏）检测。
///
/// 判定条件（需同时满足）：
///   1. 前台窗口铺满屏幕（宽高 ≥ 屏幕，容差几个像素，兼容多屏/无边框窗口）；
///   2. 窗口样式属于"无边框/弹出式"——真正的独占全屏游戏和常见无边框窗口游戏都是这一类；
///   3. 前台进程不在排除名单里。
///
/// 特别说明：浏览器进程（chrome/msedge/firefox...）被显式排除。
/// 浏览器 F11 全屏、浏览器里跑的页游都不算"游戏界面"，刘海照常工作。
/// </summary>
internal sealed class FullscreenDetector
{
    private static readonly uint SelfPid = (uint)Environment.ProcessId;

    // 浏览器：全屏视频 / 页游 / F11 都靠这些进程跑，必须排除
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "iexplore", "opera", "brave", "vivaldi",
        "360chrome", "360se", "QQBrowser", "SogouExplorer", "UCBrowser",
        "baidubrowser", "Yandex", "iron", "Waterfox"
    };

    // 系统壳层窗口，绝不当游戏
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "sihost", "SearchHost", "SearchApp",
        "StartMenuExperienceHost", "ShellExperienceHost", "LockApp", "LogonUI",
        "ApplicationFrameHost", "TextInputHost", "csrss", "winlogon", "wininit",
        "services", "lsass", "fontdrvhost", "audiodg", "NotchDock"
    };

    private readonly Dictionary<uint, string> _nameCache = new();

    /// <summary>当前前台是否为"游戏界面"（全屏且非浏览器）</summary>
    public bool IsFullscreenGame()
    {
        try
        {
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;

            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == 0 || pid == SelfPid) return false;

            var name = NameOf(pid);
            if (name.Length == 0) return false;
            if (BrowserProcesses.Contains(name) || SystemProcesses.Contains(name)) return false;

            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h))
                return false;

            if (!Native.GetWindowRect(h, out var r)) return false;

            // 取该窗口所在显示器的尺寸，支持多显示器下在副屏全屏游戏
            var monitor = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
            if (!Native.GetMonitorInfo(monitor, out var mi)) return false;
            var m = mi.Monitor;

            var w = r.Right - r.Left;
            var hgt = r.Bottom - r.Top;
            var mw = m.Right - m.Left;
            var mh = m.Bottom - m.Top;
            if (mw <= 0 || mh <= 0) return false;

            // 铺满屏幕：容差 8px，兼容 DWM 边框取整和无边框窗口的负边距
            const int tol = 8;
            bool covers = w >= mw - tol && hgt >= mh - tol;

            // 无边框 / 弹出式窗口：独占全屏和无边框窗口游戏都是这种样式；
            // 普通最大化程序带 WS_CAPTION/WS_THICKFRAME，不会误判。
            long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
            bool borderless = (style & (Native.WS_CAPTION | Native.WS_THICKFRAME)) == 0;

            return covers && borderless;
        }
        catch
        {
            return false;
        }
    }

    private string NameOf(uint pid)
    {
        if (_nameCache.TryGetValue(pid, out var cached)) return cached;
        string name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
        }
        catch
        {
            name = "";
        }
        // 只留最近一小批，避免长时间运行后缓存膨胀
        if (_nameCache.Count > 64) _nameCache.Clear();
        _nameCache[pid] = name;
        return name;
    }
}
