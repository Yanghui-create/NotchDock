using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NotchDock;

/// <summary>
/// 剪贴板监听。用 AddClipboardFormatListener + WM_CLIPBOARDUPDATE，
/// 比老的 SetClipboardViewer 链稳定，也不需要维护窗口链。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private string _lastSig = "";
    private DateTime _lastSelfWrite = DateTime.MinValue;

    /// <summary>捕获到一条新的复制内容</summary>
    public event Action<ClipItem>? Captured;

    public bool Enabled { get; set; } = true;

    public void Attach(Window window)
    {
        try
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            if (_hwnd == IntPtr.Zero) return;

            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);

            if (!AddClipboardFormatListener(_hwnd))
                Log.Write($"clip: AddClipboardFormatListener 失败 err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Ex("剪贴板监听安装失败", ex); }
    }

    public void Dispose()
    {
        try
        {
            _source?.RemoveHook(WndProc);
            if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        }
        catch { }
        _source = null;
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) ScheduleRead();
        return IntPtr.Zero;
    }

    /// <summary>剪贴板刚被写入时源程序可能还锁着它，稍微等一拍再读</summary>
    private void ScheduleRead()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(130) };
        t.Tick += (_, _) => { t.Stop(); Read(); };
        t.Start();
    }

    /// <summary>程序自己写剪贴板时不要把自己的写入又记一遍</summary>
    public void MarkSelfWrite()
    {
        _lastSelfWrite = DateTime.Now;
    }

    private void Read()
    {
        if (!Enabled) return;
        if ((DateTime.Now - _lastSelfWrite).TotalMilliseconds < 900) return;

        try
        {
            var source = ForegroundProcessName();
            if (source.StartsWith("NotchDock", StringComparison.OrdinalIgnoreCase)) source = "剪贴板";

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                var sig = "T|" + Hash(text);
                if (sig == _lastSig) return;
                _lastSig = sig;

                var kind = Classify(text);
                Captured?.Invoke(new ClipItem
                {
                    Kind = kind,
                    Text = text,
                    Source = source,
                    Time = DateTime.Now
                });
                return;
            }

            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return;

                var bytes = EncodePng(img);
                var sig = "I|" + Hash(Convert.ToBase64String(bytes));
                if (sig == _lastSig) return;
                _lastSig = sig;

                ClipStore.SaveImage(bytes, out var file);
                Captured?.Invoke(new ClipItem
                {
                    Kind = ClipKind.Image,
                    ImageFile = file,
                    Source = source,
                    Time = DateTime.Now
                });
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count == 0) return;

                var list = new List<string>();
                foreach (string? f in files) if (!string.IsNullOrEmpty(f)) list.Add(f!);
                if (list.Count == 0) return;

                var sig = "F|" + Hash(string.Join("|", list));
                if (sig == _lastSig) return;
                _lastSig = sig;

                Captured?.Invoke(new ClipItem
                {
                    Kind = ClipKind.File,
                    Files = list,
                    Source = source,
                    Time = DateTime.Now
                });
            }
        }
        catch (Exception ex) { Log.Ex("读取剪贴板失败", ex); }
    }

    // ===================== 工具 =====================

    private static string Hash(string s)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private static string ForegroundProcessName()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return "系统";
            GetWindowThreadProcessId(h, out var pid);
            if (pid == 0) return "系统";

            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
        catch { return "未知"; }
    }

    /// <summary>把剪贴板图片编码成 PNG；超大图先等比缩到 1600px 以内，避免存出几十兆</summary>
    private static byte[] EncodePng(BitmapSource src)
    {
        var width = src.PixelWidth;
        var height = src.PixelHeight;
        BitmapSource target = src;

        const int max = 1600;
        if (width > max || height > max)
        {
            var scale = max / (double)Math.Max(width, height);
            target = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
        }

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(target));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>粗分类：URL / 代码 / 普通文字</summary>
    private static ClipKind Classify(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0) return ClipKind.Text;

        if ((t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) &&
            !t.Contains('\n'))
            return ClipKind.Url;

        if (LooksLikeCode(t)) return ClipKind.Code;
        return ClipKind.Text;
    }

    private static bool LooksLikeCode(string t)
    {
        if (!t.Contains('\n')) return false;

        var markers = new[]
        {
            "function ", "=>", "def ", "class ", "import ", "#include", "public ", "private ",
            "const ", "let ", "var ", "return ", "};", "});", "<?xml", "</", "SELECT ", "npm ",
            "docker ", "git ", "#!/", "print(", "console.log", "SELECT ", "FROM "
        };

        int hits = markers.Count(m => t.Contains(m, StringComparison.Ordinal));
        if (hits >= 2) return true;

        // 分号结尾的行超过两行，也算代码
        var semicolonLines = t.Split('\n').Count(l => l.TrimEnd().EndsWith(';'));
        return semicolonLines >= 2;
    }
}
