using System.Windows.Threading;

namespace NotchDock;

/// <summary>
/// 「点一下记录，直接粘贴到刚才的光标位置」。
///
/// 前提条件：刘海窗口带 WS_EX_NOACTIVATE，点它不会抢走前台焦点，
/// 所以点卡片那一刻，前台窗口通常还是你刚才在用的那个程序——
/// 我们只要把内容写进剪贴板，再朝前台窗口发一次 Ctrl+V 就行。
///
/// 但有两种情况会让前台落到自己身上（从托盘展开时 Activate 过、打开了 ⋯ 菜单），
/// 那种时候得先把焦点还给上一个外部窗口，再发按键。所以这里持续记着
/// 「最近一个不属于本进程的前台窗口」，作为兜底目标。
/// </summary>
public sealed class FocusKeeper : IDisposable
{
    private static readonly uint SelfPid = (uint)Environment.ProcessId;

    private readonly DispatcherTimer _poll;
    private IntPtr _lastExternal;

    public FocusKeeper()
    {
        // 400ms 一次足够：记录的是「几秒内你在哪个窗口里」，不是精确时序
        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _poll.Tick += (_, _) => Sample();
        Sample();
        _poll.Start();
    }

    /// <summary>最近一个不属于本进程、且仍然有效的前台窗口</summary>
    public IntPtr LastExternal => _lastExternal;

    public void Dispose() => _poll.Stop();

    private void Sample()
    {
        try
        {
            var h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return;

            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == 0 || pid == SelfPid) return;      // 自己 / 系统假窗口，不算目标
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) return;

            _lastExternal = h;
        }
        catch { }
    }

    /// <summary>
    /// 把焦点交回目标窗口并发 Ctrl+V。
    /// 返回 false 表示找不到可用目标（窗口已关），调用方应该提示用户手动粘贴。
    /// </summary>
    public async Task<bool> PasteAsync()
    {
        var target = _lastExternal;

        // 1) 前台已经在别人手里 → 直接发键，别多此一举去 SetForegroundWindow
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out var fgPid);
        var fgIsSelf = fg == IntPtr.Zero || fgPid == SelfPid;

        if (fgIsSelf)
        {
            if (target == IntPtr.Zero || !Native.IsWindow(target)) return false;
            if (!Activate(target)) return false;

            // 给目标窗口一点时间把焦点、插入符恢复好
            await Task.Delay(140);
        }
        else
        {
            // 前台是别人，但它可能不是我们记的那个（用户中途切过窗口）——
            // 这种情况以「当前前台」为准更符合直觉，不用动焦点
            await Task.Delay(40);
        }

        SendCtrlV();
        return true;
    }

    /// <summary>
    /// SetForegroundWindow 在"调用方不是前台进程"时会被系统拒绝，
    /// 用 AttachThreadInput 把自己的输入队列临时挂到当前前台线程上可以绕过。
    /// </summary>
    private static bool Activate(IntPtr hwnd)
    {
        try
        {
            if (Native.IsIconic(hwnd)) Native.ShowWindowRestore(hwnd);

            var fg = Native.GetForegroundWindow();
            var fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
            var myThread = Native.GetCurrentThreadId();

            var attached = fgThread != 0 && fgThread != myThread &&
                           Native.AttachThreadInput(myThread, fgThread, true);
            try
            {
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(myThread, fgThread, false);
            }

            return Native.GetForegroundWindow() == hwnd;
        }
        catch (Exception ex)
        {
            Log.Ex("恢复前台窗口失败", ex);
            return false;
        }
    }

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(Native.VK_CONTROL, false),
            Key(Native.VK_V, false),
            Key(Native.VK_V, true),
            Key(Native.VK_CONTROL, true)
        };

        var sent = Native.SendInput((uint)inputs.Length, inputs,
                                    System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        if (sent != inputs.Length)
            Log.Write($"paste: SendInput 只发出 {sent}/{inputs.Length} 个事件");
    }

    private static Native.INPUT Key(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? Native.KEYEVENTF_KEYUP : 0
            }
        }
    };
}
