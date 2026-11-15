using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace HotkeyProbe;

/// <summary>
/// 第二轮探测：注册 Win+V 之后按下 Win+V，
///   1) 我们的 WM_HOTKEY 会不会到？
///   2) 系统自带的剪贴板面板还会不会弹出来？
/// 判定方式是"按下前后枚举所有可见顶层窗口，做差集"。
/// </summary>
static class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_WIN = 8, MOD_NOREPEAT = 0x4000, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;

    internal static int HotkeyHits;

    [STAThread]
    static void Main(string[] args)
    {
        // 只做注入：HotkeyProbe.exe send
        if (args.Length > 0 && args[0] == "send")
        {
            var spec = args.Length > 1 ? args[1] : "win+v";
            Console.WriteLine($"模拟按下 {spec}");
            SendCombo(spec);
            Thread.Sleep(300);
            return;
        }

        var form = new ProbeForm { Width = 460, Height = 180, Text = "HotkeyProbe2", ShowInTaskbar = false };
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(100, 100);

        form.Shown += (_, _) =>
        {
            var ok = RegisterHotKey(form.Handle, 1, MOD_WIN | MOD_NOREPEAT, 0x56);
            Console.WriteLine($"RegisterHotKey(MOD_WIN+V) = {ok}  err={Marshal.GetLastWin32Error()}");
            if (!ok) { form.Close(); return; }

            var baseline = Snapshot();
            Console.WriteLine($"基线可见窗口数: {baseline.Count}");

            Console.WriteLine("模拟按下 Win+V ...");
            SendWinV();

            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var after = Snapshot();
                var added = after.Where(k => !baseline.Contains(k)).ToList();

                Console.WriteLine($"按下后新增可见窗口: {added.Count}");
                foreach (var a in added) Console.WriteLine("  + " + a);

                Console.WriteLine(HotkeyHits > 0
                    ? "结论：Win+V 抢注成功，WM_HOTKEY 有响应"
                    : "结论：WM_HOTKEY 没响应");

                var systemPanel = added.Any(a => a.Contains("TextInputHost", StringComparison.OrdinalIgnoreCase)
                                              || a.Contains("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                                              || a.Contains("剪贴板")
                                              || a.Contains("Clipboard", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine(systemPanel
                    ? "注意：系统剪贴板面板可能也弹出来了"
                    : "系统剪贴板面板没有出现");

                form.Close();
            };
            t.Start();
        };

        Application.Run(form);
    }

    private sealed class ProbeForm : Form
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == 1)
            {
                HotkeyHits++;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] WM_HOTKEY 收到（第 {HotkeyHits} 次）");
            }
            base.WndProc(ref m);
        }
    }

    private static HashSet<string> Snapshot()
    {
        var set = new HashSet<string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;

            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            if (title.Length == 0) return true;

            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);

            GetWindowThreadProcessId(h, out var pid);
            var name = "?";
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }

            set.Add($"{name} | {cls} | {title}");
            return true;
        }, IntPtr.Zero);
        return set;
    }

    private static void SendWinV() => SendCombo("win+v");

    /// <summary>发送 ctrl+alt+k 这类组合键</summary>
    private static void SendCombo(string spec)
    {
        var mods = new List<ushort>();
        ushort main = 0;

        foreach (var part in spec.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "win": mods.Add(0x5B); break;
                case "ctrl": mods.Add(0x11); break;
                case "alt": mods.Add(0x12); break;
                case "shift": mods.Add(0x10); break;
                default:
                    var c = char.ToUpperInvariant(part.Trim()[0]);
                    main = c;
                    break;
            }
        }
        if (main == 0) { Console.WriteLine("无法解析"); return; }

        var inputs = new List<INPUT>();
        foreach (var m in mods) inputs.Add(Key(m, false));
        inputs.Add(Key(main, false));
        inputs.Add(Key(main, true));
        foreach (var m in mods.AsEnumerable().Reverse()) inputs.Add(Key(m, true));

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        Console.WriteLine($"SendInput -> {sent}/{arr.Length}");
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } }
    };
}
