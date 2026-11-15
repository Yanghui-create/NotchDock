// 验证工具：
//   Probe.exe            刘海响应延迟（鼠标进入->程序开始展开）+ 窗口是否恒定不动
//   Probe.exe passthrough 验证刘海窗口的透明区域是否真的"鼠标穿透"
//   Probe.exe watch      静默观察窗口尺寸变化（待办提醒等）
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Probe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string title);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    private const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    private static IntPtr FindNotch()
    {
        var h = FindWindow(null, "NotchDock");
        if (h != IntPtr.Zero) return h;

        var procs = Process.GetProcessesByName("NotchDock");
        if (procs.Length == 0) return IntPtr.Zero;
        var ids = procs.Select(p => (uint)p.Id).ToHashSet();

        IntPtr found = IntPtr.Zero;
        EnumWindows((w, _) =>
        {
            GetWindowThreadProcessId(w, out var pid);
            if (ids.Contains(pid) && IsWindowVisible(w)) { found = w; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "NotchDock", "notchdock.log");

    private static long LogSize()
    {
        try { return new FileInfo(LogPath).Exists ? new FileInfo(LogPath).Length : 0; }
        catch { return 0; }
    }

    private static string ReadFrom(long offset)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    // ===================== 主验证 =====================

    private static int Run()
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }

        var ex = (long)GetWindowLongPtr(h, -20);
        Console.WriteLine($"TOPMOST={(ex & 0x8) != 0}  TOOLWINDOW={(ex & 0x80) != 0}  LAYERED={(ex & 0x80000) != 0}");

        GetWindowRect(h, out var r0);
        int w0 = r0.Right - r0.Left, h0 = r0.Bottom - r0.Top;
        Console.WriteLine($"窗口矩形: {w0} x {h0} @ ({r0.Left},{r0.Top})");

        // 刘海所在位置：窗口顶部中点
        int notchX = (r0.Left + r0.Right) / 2;
        int notchY = r0.Top + 15;

        var hitTop = WindowFromPoint(new POINT { X = notchX, Y = notchY });
        var hitMid = WindowFromPoint(new POINT { X = notchX, Y = r0.Top + 300 });
        Console.WriteLine($"WindowFromPoint({notchX},{notchY}) 命中刘海={hitTop == h}   " +
                          $"WindowFromPoint({notchX},{r0.Top + 300}) 命中刘海={hitMid == h}");

        // 把光标先挪开，确保处于收起态
        SetCursorPos(60, 700);
        Thread.Sleep(500);

        // 1) 鼠标进入 -> 窗口开始变大（真实响应延迟）
        var sw = Stopwatch.StartNew();
        SetCursorPos(notchX, notchY);
        long respMs = -1;
        while (sw.ElapsedMilliseconds < 2000)
        {
            GetWindowRect(h, out var rr);
            if (rr.Bottom - rr.Top > h0 + 2) { respMs = sw.ElapsedMilliseconds; break; }
            Thread.SpinWait(60);
        }

        // 2) 动画期间窗口矩形必须一帧都不动
        var rects = new List<(int w, int h, int l, int t)>();
        var sw2 = Stopwatch.StartNew();
        while (sw2.ElapsedMilliseconds < 700)
        {
            GetWindowRect(h, out var rr);
            rects.Add((rr.Right - rr.Left, rr.Bottom - rr.Top, rr.Left, rr.Top));
            Thread.Sleep(4);
        }
        var distinct = rects.Distinct().Count();

        Console.WriteLine($"鼠标进入 -> 窗口开始变大: {respMs} ms");
        Console.WriteLine($"动画期间窗口矩形采样 {rects.Count} 次, 不同取值 {distinct} 个（动画中应为多个）");

        // 3) 离开 -> 开始收起
        sw.Restart();
        SetCursorPos(60, 700);
        long colMs = -1;
        while (sw.ElapsedMilliseconds < 2000)
        {
            GetWindowRect(h, out var rr);
            if (rr.Bottom - rr.Top < r0.Bottom - r0.Top + 400) { colMs = sw.ElapsedMilliseconds; break; }
            Thread.SpinWait(60);
        }
        Console.WriteLine($"鼠标离开 -> 开始收起: {colMs} ms");

        Console.WriteLine("--- 动画帧节奏（需以 --probe-anim 启动才有） ---");
        var log = ReadFrom(0);
        foreach (var line in log.Split('\n').Where(l => l.Contains("anim(")).TakeLast(4))
            Console.WriteLine("  " + line.Trim());

        return 0;
    }

    // ===================== 透明区域穿透验证 =====================

    private static int PassThrough()
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }

        GetWindowRect(h, out var r);
        int cx = (r.Left + r.Right) / 2;
        int probeY = r.Top + 420;               // 窗口内、刘海形状之外的透明区
        if (probeY > r.Bottom - 20) probeY = r.Bottom - 60;

        Console.WriteLine($"窗口: ({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}");
        Console.WriteLine($"测试点: ({cx},{probeY}) —— 位于窗口内但不在刘海形状上");

        var hit = WindowFromPoint(new POINT { X = cx, Y = probeY });
        GetWindowThreadProcessId(hit, out var hpid);
        string pname = "?";
        try { pname = Process.GetProcessById((int)hpid).ProcessName; } catch { }

        Console.WriteLine($"WindowFromPoint -> 0x{hit.ToInt64():X} (pid={hpid} {pname})  刘海窗口=0x{h.ToInt64():X}");
        Console.WriteLine(hit == h ? "!! 命中了刘海窗口本身（可能不穿透）" : "该点上没有刘海窗口，命中下层窗口");

        // 真点击验证：找记事本，放在刘海下方，点一下看它是否成为前台窗口
        var note = Process.Start("notepad.exe");
        Thread.Sleep(1200);
        try
        {
            var nb = Process.GetProcessesByName("notepad").FirstOrDefault();
            if (nb != null && nb.MainWindowHandle != IntPtr.Zero)
            {
                MoveWindow(nb.MainWindowHandle, r.Left + 220, r.Top + 300, 420, 220, true);
                Thread.Sleep(400);

                SetCursorPos(cx, probeY);
                SetForegroundWindow(nb.MainWindowHandle);
                Thread.Sleep(300);
                var before = GetForegroundWindow();

                mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(500);

                var fg = GetForegroundWindow();
                GetWindowThreadProcessId(fg, out var fpid);
                Console.WriteLine($"真点击后前台窗口 pid={fpid}  (记事本 pid={nb.Id})");
                Console.WriteLine(fpid == nb.Id ? "=> 点击穿透成功，落到了下层窗口" : "=> 点击被拦住，没有穿到下层");
            }
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("notepad")) { try { p.Kill(); } catch { } }
        }
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);

    // ===================== 静默观察 =====================

    private static int Watch(int seconds)
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }

        var sw = Stopwatch.StartNew();
        Console.WriteLine($"观察 {seconds}s（窗口矩形 + 日志）");
        while (sw.Elapsed.TotalSeconds < seconds) Thread.Sleep(200);
        return 0;
    }

    // ===================== 端到端：进入 -> 面板内移动 -> 离开 =====================

    private static int Full()
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }

        GetWindowRect(h, out var r);
        int cx = (r.Left + r.Right) / 2;

        string Rect() { GetWindowRect(h, out var q); return $"{q.Right - q.Left}x{q.Bottom - q.Top}@({q.Left},{q.Top})"; }

        Console.WriteLine($"1) 初始            : {Rect()}");

        SetCursorPos(60, 700);
        Thread.Sleep(600);
        Console.WriteLine($"2) 移开稳定后       : {Rect()}");

        SetCursorPos(cx, r.Top + 12);
        Thread.Sleep(900);
        Console.WriteLine($"3) 悬停 900ms 后    : {Rect()}");

        // 在面板内部移动，不应收起
        SetCursorPos(cx, r.Top + 240);
        Thread.Sleep(300);
        SetCursorPos(cx - 100, r.Top + 320);
        Thread.Sleep(1400);
        Console.WriteLine($"4) 面板内移动 1.7s  : {Rect()}");

        // 离开
        SetCursorPos(60, 700);
        Thread.Sleep(700);
        Console.WriteLine($"5) 移出 700ms 后    : {Rect()}");

        Console.WriteLine();
        Console.WriteLine("--- 最近日志 ---");
        var lines = ReadFrom(0).Split('\n').Where(l => l.Contains("expand(") || l.Contains("collapse(") || l.Contains("anim(")).TakeLast(8);
        foreach (var l in lines) Console.WriteLine("  " + l.Trim());
        return 0;
    }

    /// <summary>把光标停在刘海中心并退出（用于截图）</summary>
    private static int Park()
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }
        GetWindowRect(h, out var r);
        SetCursorPos((r.Left + r.Right) / 2, r.Top + 15);
        Console.WriteLine("parked");
        return 0;
    }

    /// <summary>验证「点刘海以外的地方自动收起」，以及「点刘海内部不收起」</summary>
    private static int Outside()
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }

        string Rect() { GetWindowRect(h, out var q); return $"{q.Right - q.Left}x{q.Bottom - q.Top}@({q.Left},{q.Top})"; }
        GetWindowRect(h, out var r);
        int cx = (r.Left + r.Right) / 2;

        Console.WriteLine($"A) 当前(应为展开 660x520) : {Rect()}");

        // 1) 点刘海内部（页签区域），不该收起
        SetCursorPos(cx, r.Top + 26);
        Thread.Sleep(150);
        Click();
        Thread.Sleep(700);
        Console.WriteLine($"B) 点刘海内部后           : {Rect()}   (应仍为 660x520)");

        // 2) 点屏幕左下角空白，应该收起
        SetCursorPos(200, 620);
        Thread.Sleep(150);
        Click();
        Thread.Sleep(700);
        Console.WriteLine($"C) 点屏幕外部后           : {Rect()}   (应为 660x30)");

        Console.WriteLine();
        Console.WriteLine("--- 日志 ---");
        foreach (var l in ReadFrom(0).Split('\n').Where(x => x.Contains("expand(") || x.Contains("collapse(")).TakeLast(4))
            Console.WriteLine("  " + l.Trim());
        return 0;
    }

    private static void Click()
    {
        mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
    }

    /// <summary>验证 pinned（托盘展开）状态下：光标在外部停留不收，点一下才收</summary>
    private static int Pinned(int x, int y)
    {
        var h = FindNotch();
        if (h == IntPtr.Zero) { Console.WriteLine("NOT_FOUND"); return 1; }

        string Rect() { GetWindowRect(h, out var q); return $"{q.Right - q.Left}x{q.Bottom - q.Top}@({q.Left},{q.Top})"; }

        SetCursorPos(x, y);   // 先把光标放到刘海外面

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 10000)
        {
            GetWindowRect(h, out var q);
            if (q.Bottom - q.Top > 400) break;
            Thread.Sleep(40);
        }
        Console.WriteLine($"1) 托盘展开后            : {Rect()}   (应 660x520)");

        Thread.Sleep(1600);
        Console.WriteLine($"2) 光标在外停留 1.6s     : {Rect()}   (仍应 660x520，不该自己收)");

        Click();
        Thread.Sleep(700);
        Console.WriteLine($"3) 在外部点一下之后      : {Rect()}   (应 660x30)");

        Console.WriteLine();
        foreach (var l in ReadFrom(0).Split('\n').Where(t => t.Contains("expand(") || t.Contains("collapse(")).TakeLast(4))
            Console.WriteLine("  " + l.Trim());
        return 0;
    }

    /// <summary>列出某个进程的全部顶层窗口（含隐藏），看它有没有独立的通知弹窗</summary>
    private static int Windows(string procName)
    {
        var procs = Process.GetProcessesByName(procName);
        if (procs.Length == 0) { Console.WriteLine("NO_PROCESS"); return 1; }
        var ids = procs.Select(p => (uint)p.Id).ToHashSet();

        Console.WriteLine($"pid: {string.Join(",", ids)}");

        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!ids.Contains(pid)) return true;

            var title = new System.Text.StringBuilder(256);
            GetWindowText(h, title, 256);
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            GetWindowRect(h, out var r);

            Console.WriteLine($"  vis={IsWindowVisible(h),-5} pid={pid,-6} class={cls.ToString(),-34} " +
                              $"rect=({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}) title='{title}'");
            return true;
        }, IntPtr.Zero);
        return 0;
    }

    /// <summary>把光标移到某点并点一下（用于点页签、点卡片）</summary>
    private static int ClickAt(int x, int y, int settle = 400)
    {
        SetCursorPos(x, y);
        Thread.Sleep(settle);
        Click();
        Thread.Sleep(settle);
        Console.WriteLine($"clicked ({x},{y})");
        return 0;
    }

    private static int Main(string[] argv)
    {
        if (argv.Length >= 3 && argv[0] == "click") return ClickAt(int.Parse(argv[1]), int.Parse(argv[2]));
        if (argv.Length >= 2 && argv[0] == "windows") return Windows(argv[1]);
        if (argv.Length >= 3 && argv[0] == "pinned") return Pinned(int.Parse(argv[1]), int.Parse(argv[2]));
        if (argv.Length >= 1 && argv[0] == "outside") return Outside();
        if (argv.Length >= 1 && argv[0] == "park") return Park();
        if (argv.Length >= 1 && argv[0] == "full") return Full();
        if (argv.Length >= 1 && argv[0] == "passthrough") return PassThrough();
        if (argv.Length >= 2 && argv[0] == "watch") return Watch(int.Parse(argv[1]));
        return Run();
    }
}
