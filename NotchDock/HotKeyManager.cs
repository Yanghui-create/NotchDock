using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NotchDock;

/// <summary>
/// 全局热键。
///
/// 为什么不用低级键盘钩子硬抢：实测 Win11 上 `RegisterHotKey(MOD_WIN, V)` 是**能成功**的
/// （Win+V 并不在 explorer 的 RegisterHotKey 名单里，系统剪贴板面板走的是 shell 自己的
/// 哨兵逻辑，只要有人注册了这组键，shell 就不再弹它）。注册比钩子干净得多：
///   · 不吞别人的输入，不会让系统误判 Win 键还按着；
///   · 不占用键盘消息链，对游戏/输入法零干扰；
///   · 按键重复可用 MOD_NOREPEAT 关掉。
/// 只有"录制新组合键"这一步必须用钩子——刘海窗口是 NOACTIVATE 的，拿不到键盘焦点，
/// 想让用户按一下就抓到键，只能短暂挂一个 WH_KEYBOARD_LL。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private const int HotKeyId = 0x4E44;          // "ND"

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;   // 构造在 UI 线程
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    /// <summary>热键触发（已切回 UI 线程）</summary>
    public event Action? Pressed;

    /// <summary>正在录制新组合键（这期间不响应热键本身）</summary>
    public bool Capturing { get; private set; }

    public HotKey Current { get; private set; } = HotKey.Default;
    public bool IsRegistered => _registered;

    public void Attach(Window window)
    {
        try
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            if (_hwnd == IntPtr.Zero) return;

            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }
        catch (Exception ex) { Log.Ex("热键挂载失败", ex); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            if (!Capturing) Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    /// <summary>注册一组热键；失败时返回原因（用于界面提示）</summary>
    public bool TryApply(HotKey hk, out string? error)
    {
        error = null;
        Unregister();

        if (!hk.IsValid)
        {
            error = "组合键不完整，必须包含至少一个修饰键";
            return false;
        }

        if (_hwnd == IntPtr.Zero)
        {
            error = "窗口尚未就绪";
            return false;
        }

        Current = hk;

        if (Native.RegisterHotKey(_hwnd, HotKeyId, (uint)hk.Mods | Native.MOD_NOREPEAT, hk.Vk))
        {
            _registered = true;
            Log.Write($"hotkey: 已注册 {hk.Display}");
            return true;
        }

        var err = Marshal.GetLastWin32Error();
        error = err == Native.ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"「{hk.Pretty}」已被其它程序占用"
            : $"注册失败（错误码 {err}）";
        Log.Write($"hotkey: 注册 {hk.Display} 失败 err={err}");
        return false;
    }

    public void Unregister()
    {
        if (!_registered) return;
        try { Native.UnregisterHotKey(_hwnd, HotKeyId); } catch { }
        _registered = false;
    }

    public void Dispose()
    {
        EndCapture(null);
        Unregister();
        try { _source?.RemoveHook(WndProc); } catch { }
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    // ===================== 录制新组合键（临时键盘钩子） =====================

    private IntPtr _capHook = IntPtr.Zero;
    private Native.LowLevelMouseProc? _capProc;      // 与键盘钩子签名相同，直接复用
    private Action<HotKey?>? _capDone;
    private DispatcherTimer? _capTimer;

    /// <summary>
    /// 开始录制。用户按下的第一个"修饰键 + 主键"组合会被截获并返回；
    /// 按 Esc 或 10 秒无操作则返回 null。
    /// </summary>
    public void BeginCapture(Action<HotKey?> done)
    {
        if (Capturing) EndCapture(null);

        Capturing = true;
        _capDone = done;

        // 录制期间先注销，否则按到当前热键会先触发动作（面板闪一下）
        Unregister();

        _capProc = CaptureProc;
        _capHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _capProc,
                                           Native.GetModuleHandle(null), 0);
        if (_capHook == IntPtr.Zero)
        {
            Log.Write($"hotkey: 录制钩子安装失败 err={Marshal.GetLastWin32Error()}");
            EndCapture(null);
            return;
        }

        _capTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _capTimer.Tick += (_, _) => EndCapture(null);
        _capTimer.Start();
    }

    public void EndCapture(HotKey? result)
    {
        if (!Capturing) return;
        Capturing = false;

        _capTimer?.Stop();
        _capTimer = null;

        if (_capHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_capHook);
            _capHook = IntPtr.Zero;
        }
        _capProc = null;

        var done = _capDone;
        _capDone = null;
        done?.Invoke(result);
    }

    private IntPtr CaptureProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_capHook, nCode, wParam, lParam);

        var msg = (int)wParam;
        if (msg != Native.WM_KEYDOWN && msg != Native.WM_SYSKEYDOWN)
            return Native.CallNextHookEx(_capHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        var vk = info.vkCode;

        // 修饰键本身不结束录制，等主键。
        // 注意：低级键盘钩子上报的是**左右分开**的键码（0xA0~0xA5），
        // 只过滤通用码（0x10~0x12）会把左 Alt 当成主键录进去（踩过）。
        if (IsModifierVk(vk))
            return Native.CallNextHookEx(_capHook, nCode, wParam, lParam);

        if (vk == Native.VK_ESCAPE)
        {
            _ui.BeginInvoke(new Action(() => EndCapture(null)));
            return (IntPtr)1;
        }

        var mods = CurrentMods();
        if (mods == HotKeyMods.None)
            return Native.CallNextHookEx(_capHook, nCode, wParam, lParam);   // 纯字母不算，别打断打字

        var hk = new HotKey(mods, vk);
        _ui.BeginInvoke(new Action(() => EndCapture(hk)));
        return (IntPtr)1;   // 吞掉这一次按键，别让目标程序吃到一个莫名其妙的 V
    }

    private static bool IsModifierVk(uint vk) => vk is
        0x10 or 0x11 or 0x12 or          // Shift / Ctrl / Alt（通用）
        0xA0 or 0xA1 or                  // LShift / RShift
        0xA2 or 0xA3 or                  // LCtrl / RCtrl
        0xA4 or 0xA5 or                  // LAlt / RAlt
        0x5B or 0x5C;                    // LWin / RWin

    private static HotKeyMods CurrentMods()
    {
        var mods = HotKeyMods.None;
        if (IsDown(Native.VK_CONTROL)) mods |= HotKeyMods.Ctrl;
        if (IsDown(Native.VK_MENU)) mods |= HotKeyMods.Alt;
        if (IsDown(Native.VK_SHIFT)) mods |= HotKeyMods.Shift;
        if (IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN)) mods |= HotKeyMods.Win;
        return mods;
    }

    private static bool IsDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
}
