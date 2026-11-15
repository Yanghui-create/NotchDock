using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NotchDock;

public partial class NotchWindow : Window
{
    private const double CollapsedWidth = 214;
    private const double CollapsedHeight = 30;
    private const double CornerRadiusValue = 24;

    // macOS 刘海手感：宽度先起、高度稍滞后、内容最后淡入并轻微下落
    private const int ExpandWidthMs = 300;
    private const int ExpandHeightMs = 340;
    private const int CollapseMs = 220;

    private readonly ClipboardWatcher _clipWatcher = new();
    private readonly FocusKeeper _focus = new();
    private readonly HotKeyManager _hotKey = new();
    private readonly ObservableCollection<ClipItem> _clipItems = new();
    private readonly TodoScheduler _scheduler;

    private readonly ObservableCollection<FavoriteItem> _fileFavs = new();
    private readonly ObservableCollection<FavoriteItem> _appFavs = new();
    private readonly ObservableCollection<TodoItem> _todos = new();
    private readonly ObservableCollection<FavoriteItem> _pickerItems = new();

    private DispatcherTimer _guard = null!;
    private DispatcherTimer _autoCollapse = null!;
    private DispatcherTimer _clock = null!;

    private bool _expanded;
    private bool _pinned;
    private bool _menuOpen;
    private bool _dialogOpen;
    private bool _previewMode;
    private bool _alertMode;
    private int _tab;
    private string _clipFilter = "all";
    private int _animToken;
    private long _expandTick;

    /// <summary>
    /// 剪贴板列表是否需要重建。展开/切到剪贴板页时，没有新内容就原样显示——
    /// 之前每次展开都无条件 RebuildClips()，几百条记录会整表重建并实例化几百张卡片，
    /// 正好卡在展开动画那一帧上（这就是「停在剪贴板页，再展开掉帧」的主因）。
    /// </summary>
    private bool _clipsDirty;
    private DispatcherTimer? _pickerDebounce;
    private DispatcherTimer? _hintTimer;

    // 窗口尺寸动画状态
    private double _winW = CollapsedWidth;
    private double _winH = CollapsedHeight;
    private bool _shellAnimating;
    private long _animStartTicks;
    private double _toW, _toH;
    private IEasingFunction _ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private Action? _animDone;

    public NotchWindow()
    {
        InitializeComponent();

        _scheduler = new TodoScheduler(Store.Config.Todos);
        _scheduler.Fired += OnTodoFired;

        FileList.ItemsSource = _fileFavs;
        AppList.ItemsSource = _appFavs;
        TodoList.ItemsSource = _todos;
        ClipList.ItemsSource = _clipItems;
        PickerList.ItemsSource = _pickerItems;

        // 应用库搜索防抖（只订阅一次）
        _pickerDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _pickerDebounce.Tick += PickerDebounce_Tick;

        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
    }

    // ===================== 生命周期 =====================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口固定为展开态尺寸，之后永不变动；刘海形状由 Shell 自己动画
        SetupWindow();

        Root.Focusable = false;
        TodoTitle.TextChanged += (_, _) => UpdateTitleHint();

        foreach (var t in Store.Config.Todos) _todos.Add(t);
        RebuildFavorites();
        ResetTodoInputs();
        UpdateTodoFooter();
        SetTab(0);
        Theme.Apply(Store.Config.Theme);
        SyncGlassLayer();

        // 收藏图标在后台预热：SHGetFileInfo 逐个取图标是磁盘+COM 调用，
        // 放在 UI 线程会让首帧布局变卡（速览页和应用库都受益）。
        var favPaths = Store.Config.Favorites.Select(f => f.Target).ToList();
        _ = Task.Run(() => IconService.Prewarm(favPaths));

        // 应用库同样在后台预热（启动 3 秒后，避开开机竞争）：
        // 等用户点「＋ 应用」时，索引和图标一般都已经就绪，浮层秒出。
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            try
            {
                var apps = await ScanAppsAsync();
                await WarmIconsAsync(apps);
            }
            catch (Exception ex) { Log.Ex("应用库预热失败", ex); }
        });

        // 定时器必须先建好，再挂鼠标事件
        _guard = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
        _guard.Tick += (_, _) => GuardTick();
        _guard.Start();

        _autoCollapse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoCollapse.Tick += (_, _) =>
        {
            _autoCollapse.Stop();
            _alertMode = false;
            if (!_expanded || IsPointerInside() || _pinned) return;
            Collapse("alert-timeout");
        };

        // 每 30 秒刷新相对时间
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) =>
        {
            foreach (var c in _clipItems) c.Touch();
            foreach (var t in _todos) t.Touch();
            UpdateTodoFooter();
        };
        _clock.Start();

        // 置顶权重申：别的置顶窗（全屏游戏、某些播放器、悬浮工具条）会压过来，
        // 每 3 秒用 SWP_NOACTIVATE 把刘海重新抬到最上层，不抢焦点、不打扰前台程序。
        var topmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        topmost.Tick += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch { }
        };
        topmost.Start();

        // 悬停即展开（零延迟）、离开即收起
        Shell.MouseEnter += (_, _) =>
        {
            if (FrameProbeEnabled) Log.Write("mouse-enter(shell)");
            if (_alertMode) _alertMode = false;   // 用户接管提醒面板
            if (!_expanded) Expand(false, "hover");
        };
        Shell.MouseLeave += (_, _) => OnPointerLeft("leave");

        if (FrameProbeEnabled)
        {
            int moves = 0;
            Root.PreviewMouseMove += (_, _) =>
            {
                if (moves++ < 6) Log.Write($"root-mousemove #{moves} over={Root.IsMouseOver} shell={Shell.IsMouseOver}");
            };

            var geo = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            geo.Tick += (_, _) =>
            {
                geo.Stop();
                try
                {
                    var tl = Shell.PointToScreen(new Point(0, 0));
                    var br = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));
                    Log.Write($"geo: win={Width}x{Height}@{Left},{Top} shell={Shell.ActualWidth}x{Shell.ActualHeight} " +
                              $"screen=({tl.X},{tl.Y})-({br.X},{br.Y}) clip={(Shell.Clip as RectangleGeometry)?.Rect}");
                }
                catch (Exception ex) { Log.Ex("geo", ex); }
            };
            geo.Start();
        }

        _scheduler.Refresh(_todos);
        _scheduler.Start();

        // 剪贴板历史
        ClipStore.Load();
        _clipWatcher.Attach(this);
        _clipWatcher.Captured += OnClipCaptured;
        RebuildClips();
        RefreshClipFilterVisual();

        // 全局热键（默认 Win+V 接管系统剪贴板历史）
        _hotKey.Attach(this);
        _hotKey.Pressed += OnHotKeyPressed;
        ApplyHotKeyFromConfig();

        // 预热：把展开态视觉树先布局一次，消掉首次悬停的 JIT/模板开销
        _ = Dispatcher.BeginInvoke(new Action(PreWarm), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>让第一次悬停也是毫秒级：内容尺寸恒定，这里只需把所有页签的视觉树建一遍</summary>
    private void PreWarm()
    {
        try
        {
            ContentHost.Opacity = 1;
            PaneFav.Visibility = Visibility.Visible;
            PaneClip.Visibility = Visibility.Visible;
            PaneTodo.Visibility = Visibility.Visible;
            ContentHost.UpdateLayout();
        }
        catch (Exception ex) { Log.Ex("预热失败", ex); }
        finally
        {
            PaneClip.Visibility = Visibility.Collapsed;
            PaneTodo.Visibility = Visibility.Collapsed;
            ContentHost.Opacity = 0;
            SetTab(_tab);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = 1;
            Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

            // 不进 Alt+Tab / 任务视图
            var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_TOOLWINDOW));
        }
        catch (Exception ex2) { Log.Ex("窗口样式初始化失败", ex2); }
    }

    /// <summary>展开后的目标宽度（按屏幕宽度收敛）</summary>
    private double ExpW => Math.Min(Store.Config.ExpandedWidth,
                                   Math.Max(360, SystemParameters.PrimaryScreenWidth - 24));

    /// <summary>展开后的目标高度（按屏幕高度收敛）</summary>
    private double ExpH => Math.Min(Store.Config.ExpandedHeight,
                                   Math.Max(240, SystemParameters.PrimaryScreenHeight - 60));

    /// <summary>
    /// 窗口宽度恒定 = 展开宽度并水平居中，之后 x 与宽度都不再改变；
    /// 只有高度和刘海自身宽度参与动画。位置不动 → 不可能出现"尺寸变了、位置没跟上"的错位帧，
    /// 同时窗口始终贴合刘海，不会留下大片透明区去挡鼠标。
    /// </summary>
    private void SetupWindow()
    {
        ContentHost.Width = ExpW;
        ContentHost.Height = ExpH;

        StopShellAnim(false);
        BeginAnimation(FrameworkElement.HeightProperty, null);

        Width = ExpW;
        Height = CollapsedHeight;
        Left = Math.Round((SystemParameters.PrimaryScreenWidth - ExpW) / 2);
        Top = 0;

        _winW = ExpW;
        _winH = CollapsedHeight;

        Shell.Width = CollapsedWidth;
        Shell.CornerRadius = new CornerRadius(0, 0, CornerRadiusValue, CornerRadiusValue);
    }

    public void Reposition()
    {
        SetupWindow();
        if (_expanded)
        {
            Shell.Width = ExpW;
            Height = ExpH;
        }
    }

    // ===================== 展开 / 收起 =====================

    private void Expand(bool byUser, string reason = "")
    {
        if (_expanded) return;
        _expanded = true;
        if (byUser) _pinned = true;

        Log.Write($"expand({reason})");
        _autoCollapse.Stop();
        _animToken++;
        _expandTick = Environment.TickCount64;
        if (FrameProbeEnabled) BeginAnimProbe("expand");

        ContentHost.Visibility = Visibility.Visible;
        ContentHost.IsHitTestVisible = true;
        InstallMouseHook();

        // 宽度先起、高度稍滞后 → 先横向拉开再落下，接近 macOS 刘海的手感
        StartShellAnim(ExpW, ExpH, ExpandWidthMs,
                       new QuadraticEase { EasingMode = EasingMode.EaseOut });

        // 内容稍后浮现并轻微下落，避免"空壳先开、内容突然出现"的割裂感
        AnimateContent(1, 210, 40);
        AnimateContentShift(0, 300, 30);
        AnimateCollapsedDots(0, 110, 0);

        if (_tab == 1 && _clipsDirty)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RebuildClips();
            Log.Write($"clip: 展开时重建 {_clipItems.Count} 条，耗时 {sw.ElapsedMilliseconds}ms");
        }
        RefreshTabVisual();
    }

    private void Collapse(string reason = "")
    {
        if (!_expanded) return;
        _expanded = false;
        _pinned = false;
        _alertMode = false;
        _autoCollapse.Stop();
        Log.Write($"collapse({reason})");

        var token = ++_animToken;
        UninstallMouseHook();
        if (FrameProbeEnabled) BeginAnimProbe("collapse");

        // 内容先淡出并上移，刘海随后收拢，最后圆点浮出
        AnimateContent(0, 110, 0);
        AnimateContentShift(-14, 150, 0);
        StartShellAnim(CollapsedWidth, CollapsedHeight, CollapseMs,
                       new CubicEase { EasingMode = EasingMode.EaseInOut }, () =>
        {
            if (token != _animToken || _expanded) return;   // 期间又被展开了
            ContentHost.Visibility = Visibility.Collapsed;
            ContentHost.IsHitTestVisible = false;
            PickerOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.Visibility = Visibility.Collapsed;
        });
        AnimateCollapsedDots(1, 150, 130);

        if (IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }
    }

    // ===================== 尺寸动画 =====================

    /// <summary>刘海宽度 + 窗口高度同时做动画（窗口宽度与位置恒定不动）</summary>
    private void StartShellAnim(double toW, double toH, int ms,
                                IEasingFunction ease, Action? done = null)
    {
        StopShellAnim(false);

        _toW = toW;
        _toH = toH;
        _ease = ease;
        _animDone = done;
        _shellAnimating = true;
        _animStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // 刘海宽度（Shell 在恒定宽度的窗口里居中）
        var wa = new DoubleAnimation(toW, TimeSpan.FromMilliseconds(ms))
        { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        Shell.BeginAnimation(FrameworkElement.WidthProperty, wa);

        // 窗口高度（WPF 自己管 layered surface，不能用 SetWindowPos 绕过它）
        var ha = new DoubleAnimation(toH, TimeSpan.FromMilliseconds((int)(ms * 1.12)))
        { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        ha.Completed += (_, _) => StopShellAnim(true);
        BeginAnimation(FrameworkElement.HeightProperty, ha);
    }

    private void StopShellAnim(bool snap)
    {
        if (!_shellAnimating) return;
        _shellAnimating = false;

        var done = _animDone;
        _animDone = null;

        if (snap)
        {
            Shell.BeginAnimation(FrameworkElement.WidthProperty, null);
            BeginAnimation(FrameworkElement.HeightProperty, null);
            Shell.Width = _toW;
            Height = _toH;
            _winH = _toH;
        }

        done?.Invoke();
    }

    /// <summary>内容层轻微下落（展开）/ 上移（收起），给动画一点重量感</summary>
    private void AnimateContentShift(double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        ContentShift.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void AnimateContent(double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        ContentHost.BeginAnimation(OpacityProperty, a);
    }

    private void AnimateCollapsedDots(double to, int ms, int delayMs)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        CollapsedLayer.BeginAnimation(OpacityProperty, a);
    }

    // ===================== 动画帧率探针（--probe-anim 时启用） =====================

    public static bool FrameProbeEnabled;

    private string? _probeTag;
    private int _probeFrames;
    private long _probeLast;
    private long _probeMaxGap;
    private DispatcherTimer? _probeTimer;

    private void BeginAnimProbe(string tag)
    {
        EndAnimProbe();
        _probeTag = tag;
        _probeFrames = 0;
        _probeLast = 0;
        _probeMaxGap = 0;

        CompositionTarget.Rendering += OnProbeFrame;
        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _probeTimer.Tick += (_, _) => EndAnimProbe();
        _probeTimer.Start();
    }

    private void OnProbeFrame(object? sender, EventArgs e)
    {
        var now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
                         / System.Diagnostics.Stopwatch.Frequency);
        if (_probeLast > 0)
        {
            var gap = now - _probeLast;
            if (gap > _probeMaxGap) _probeMaxGap = gap;
        }
        _probeLast = now;
        _probeFrames++;
    }

    private void EndAnimProbe()
    {
        if (_probeTag == null) return;
        _probeTimer?.Stop();
        _probeTimer = null;
        CompositionTarget.Rendering -= OnProbeFrame;
        Log.Write($"anim({_probeTag}): {_probeFrames} 帧 / 最长帧间隔 {_probeMaxGap}ms");
        _probeTag = null;
    }

    /// <summary>60ms 哨兵：鼠标离开就收起（点击外部由全局钩子负责，见下）</summary>
    private void GuardTick()
    {
        if (!_expanded) return;
        if (!IsPointerInside()) OnPointerLeft("guard");
    }

    // ===================== 全局点击：点刘海以外任何地方都收起 =====================

    private IntPtr _mouseHook = IntPtr.Zero;
    private Native.LowLevelMouseProc? _mouseProc;

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseHookCallback;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc,
                                             Native.GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Log.Write($"鼠标钩子安装失败 err={Marshal.GetLastWin32Error()}");
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        _mouseProc = null;
    }

    /// <summary>钩子回调必须立刻返回，真正的判断丢回 UI 线程做</summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                int x = info.pt.X, y = info.pt.Y;
                Dispatcher.BeginInvoke(new Action(() => OnGlobalClick(x, y)));
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void OnGlobalClick(int x, int y)
    {
        if (!_expanded) return;
        // 展开那一瞬间的点击来自托盘菜单，不算"点了外面"
        if (Environment.TickCount64 - _expandTick < 250) return;
        if (_menuOpen || _dialogOpen) return;
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        if (IsPointInShell(x, y)) return;

        Collapse("click-outside");
    }

    // ===================== 外观主题 =====================

    public string CurrentTheme => Theme.Normalize(Store.Config.Theme);

    /// <summary>切换外观：black / white / glass</summary>
    public void ApplyTheme(string? kind)
    {
        var k = Theme.Normalize(kind);
        Store.Config.Theme = k;
        Theme.Apply(k);
        SyncGlassLayer();
        RefreshTabVisual();     // 页签底色是代码里赋的 Brush 对象，换主题后要重取
        if (!_previewMode) Store.Save();
        FlashHint($"外观已切换为「{Theme.DisplayName(k)}」");
    }

    private void SyncGlassLayer()
    {
        GlassLayer.Visibility = Theme.Normalize(Store.Config.Theme) == Theme.Glass
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>屏幕坐标是否落在刘海本体当前占用的区域内</summary>
    private bool IsPointInShell(int x, int y, int pad = 0)
    {
        try
        {
            var tl = Shell.PointToScreen(new Point(0, 0));
            var br = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));
            return x >= tl.X - pad && x <= br.X + pad && y >= tl.Y - pad && y <= br.Y + pad;
        }
        catch { return true; }
    }

    public void ToggleByUser()
    {
        if (_expanded) { _pinned = false; Collapse("user-toggle"); }
        else Expand(true, "user-toggle");
    }

    private void OnPointerLeft(string reason)
    {
        if (!_expanded || _pinned) return;
        // 只挡展开瞬间那一两帧的边界抖动，过了就立刻响应"离开"
        if (Environment.TickCount64 - _expandTick < 60) return;
        if (_alertMode) return;              // 提醒态不受鼠标离开影响，等 8 秒自动收
        if (IsPointerInside()) return;
        if (_menuOpen || _dialogOpen) return;
        if (PickerOverlay.Visibility == Visibility.Visible) return;
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        Collapse(reason);
    }

    private bool IsPointerInside()
    {
        try
        {
            if (Shell.IsMouseOver) return true;
            if (!Native.GetCursorPos(out var p)) return false;

            // 窗口尺寸 = 刘海尺寸（动画中也是），所以直接用 Shell 的屏幕区域判定
            return IsPointInShell(p.X, p.Y, 6);
        }
        catch { return true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 兜底落盘：每条增删都已经即时保存过，这里再写一次，确保异常退出也不丢历史
        try { ClipStore.Save(); } catch { }
        UninstallMouseHook();
        _clipWatcher.Dispose();
        _focus.Dispose();
        _hotKey.Dispose();
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible) { CloseSettings(); e.Handled = true; return; }
            if (PickerOverlay.Visibility == Visibility.Visible) { ClosePicker(); e.Handled = true; return; }
            _pinned = false;
            Collapse("esc");
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    // ===================== 页签 =====================

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag?.ToString(), out var i)) SetTab(i);
    }

    // ===================== 全局快捷键 =====================

    private void OnHotKeyPressed()
    {
        if (_hotKey.Capturing) return;

        Log.Write("hotkey: 触发");

        // 已经在剪贴板页 → 再按一次收起来；否则直接展开到剪贴板页。
        // 用 Expand(true)（pinned）：鼠标此刻可能在屏幕任何地方，
        // 不能让它被"鼠标离开就收起"的守卫立刻收掉，靠点外部 / Esc / 再按一次收。
        if (_expanded && _tab == 1 && !SettingsOverlay.Visibility.Equals(Visibility.Visible))
        {
            _pinned = false;
            Collapse("hotkey");
            return;
        }

        if (SettingsOverlay.Visibility == Visibility.Visible) CloseSettings();
        if (PickerOverlay.Visibility == Visibility.Visible) ClosePicker();

        _alertMode = false;
        Expand(true, "hotkey");
        SetTab(1);
    }

    private void ApplyHotKeyFromConfig()
    {
        if (!Store.Config.HotKeyEnabled)
        {
            _hotKey.Unregister();
            RefreshHotKeyUi();
            return;
        }

        if (!HotKey.TryParse(Store.Config.HotKey, out var hk)) hk = HotKey.Default;

        if (!_hotKey.TryApply(hk, out var error))
        {
            Log.Write($"hotkey: {error}");
        }

        RefreshHotKeyUi();
    }

    /// <summary>把当前键位与状态刷到设置界面</summary>
    private void RefreshHotKeyUi()
    {
        if (HotKeyText == null) return;

        var enabled = Store.Config.HotKeyEnabled;
        var hk = _hotKey.Current;

        HotKeyText.Text = hk.IsValid ? hk.Pretty : "未设置";
        HotKeyToggle.IsChecked = enabled;

        if (!enabled)
        {
            HotKeyStatus.Text = "已关闭";
            HotKeyStatus.Foreground = (Brush)FindResource("FgMuteBrush");
        }
        else if (_hotKey.IsRegistered)
        {
            HotKeyStatus.Text = hk.Equals(HotKey.WinV) ? "已生效 · 已接管 Win+V" : "已生效";
            HotKeyStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            HotKeyStatus.Text = "注册失败，可能被其它程序占用";
            HotKeyStatus.Foreground = (Brush)FindResource("DangerBrush");
        }

        RefreshWinVButton();
    }

    /// <summary>
    /// 剪贴板页右上角那个「Win+V 接管」按钮的状态：
    /// 当前热键正是 Win+V 且注册成功 → 绿色「Win+V 已接管」；否则是中性色的「接管 Win+V」。
    /// </summary>
    private void RefreshWinVButton()
    {
        if (BtnWinV == null) return;

        var on = Store.Config.HotKeyEnabled && _hotKey.IsRegistered && _hotKey.Current.Equals(HotKey.WinV);
        BtnWinV.Content = on ? "Win+V 已接管" : "接管 Win+V";
        BtnWinV.Foreground = on ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("FgDimBrush");
        BtnWinV.ToolTip = on
            ? "Win+V 已由 NotchDock 接管，按下即呼出剪贴板"
            : "点击接管 Win+V（系统剪贴板历史那组键）";
    }

    /// <summary>点了「接管 Win+V」：直接把这组键注册过来，不用进设置里改</summary>
    private void BtnWinV_Click(object sender, RoutedEventArgs e)
    {
        if (Store.Config.HotKeyEnabled && _hotKey.IsRegistered && _hotKey.Current.Equals(HotKey.WinV))
        {
            FlashHint("Win+V 已经在接管中");
            return;
        }

        if (TrySaveHotKey(HotKey.WinV))
            FlashHint("已接管 Win+V，按下即可呼出剪贴板");
        else
            FlashHint("Win+V 被占用了，换个快捷键试试");
    }

    public void ShowSettings()
    {
        if (!_expanded) Expand(true, "settings");
        PickerOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Visible;
        ChkAutoStart.IsChecked = AutoStart.IsEnabled();
        RefreshSettingsThemeVisual();
        RefreshHotKeyUi();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void CloseSettings()
    {
        _hotKey.EndCapture(null);
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    // ===================== 设置面板：外观 / 通用 =====================

    private void SettingsTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string kind)
        {
            ApplyTheme(kind);
            RefreshSettingsThemeVisual();
        }
    }

    private void RefreshSettingsThemeVisual()
    {
        var pills = new[] { BtnThemeBlack, BtnThemeWhite, BtnThemeGlass };
        if (pills.Any(p => p == null)) return;

        var selBg = (Brush)FindResource("SelFillBrush");
        var selFg = (Brush)FindResource("SelFgBrush");
        var idleFg = (Brush)FindResource("FgDimBrush");
        var current = Theme.Normalize(Store.Config.Theme);

        foreach (var p in pills)
        {
            var on = Theme.Normalize(p.Tag?.ToString()) == current;
            p.Background = on ? selBg : Brushes.Transparent;
            p.Foreground = on ? selFg : idleFg;
            p.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
        => SetAutoStart(ChkAutoStart.IsChecked == true);

    private void AutoStartLabel_Click(object sender, MouseButtonEventArgs e)
    {
        ChkAutoStart.IsChecked = !(ChkAutoStart.IsChecked == true);
        SetAutoStart(ChkAutoStart.IsChecked == true);
    }

    private void RescanApps_Click(object sender, RoutedEventArgs e)
    {
        AppIndexer.ClearCache();
        ReloadApps();
        FlashSettingsHint("已重新扫描已安装应用");
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        Launcher.OpenPath(Store.Dir, true);
        FlashSettingsHint("已打开数据目录");
    }

    private void QuitApp_Click(object sender, RoutedEventArgs e) => (Application.Current as App)?.ExitApp();

    /// <summary>设置面板顶部的一行瞬时反馈（自动消失）</summary>
    private void FlashSettingsHint(string text)
    {
        if (SettingsHint == null) return;
        SettingsHint.Text = text;
        SettingsHint.Visibility = Visibility.Visible;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SettingsHint.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private async void RecordHotKey_Click(object sender, RoutedEventArgs e)
    {
        if (_hotKey.Capturing) { _hotKey.EndCapture(null); return; }

        BtnRecord.Content = "按组合键…";
        HotKeyStatus.Text = "请按下新的组合键（Esc 取消）";
        HotKeyStatus.Foreground = (Brush)FindResource("AccentBrush");

        var tcs = new TaskCompletionSource<HotKey?>();
        _hotKey.BeginCapture(hk => tcs.TrySetResult(hk));
        var result = await tcs.Task;

        BtnRecord.Content = "录制";

        if (result is { } captured)
        {
            if (TrySaveHotKey(captured))
                FlashHint($"快捷键已改为 {captured.Pretty}");
        }
        else
        {
            RefreshHotKeyUi();
        }
    }

    private void ResetHotKey_Click(object sender, RoutedEventArgs e)
    {
        if (TrySaveHotKey(HotKey.Default))
            FlashHint($"快捷键已恢复为 {HotKey.Default.Pretty}");
    }

    private bool TrySaveHotKey(HotKey hk)
    {
        Store.Config.HotKey = hk.Display;
        Store.Config.HotKeyEnabled = true;

        if (!_hotKey.TryApply(hk, out var error))
        {
            RefreshHotKeyUi();
            FlashHint(error ?? "注册失败");
            return false;
        }

        if (!_previewMode) Store.Save();
        RefreshHotKeyUi();
        return true;
    }

    private void HotKeyToggle_Click(object sender, RoutedEventArgs e) => ToggleHotKey();

    private void HotKeyLabel_Click(object sender, MouseButtonEventArgs e) => ToggleHotKey();

    private void ToggleHotKey()
    {
        var next = !Store.Config.HotKeyEnabled;
        Store.Config.HotKeyEnabled = next;
        if (!_previewMode) Store.Save();

        if (next) ApplyHotKeyFromConfig();
        else { _hotKey.Unregister(); RefreshHotKeyUi(); }

        FlashHint(next ? "全局快捷键已启用" : "全局快捷键已关闭");
    }

    private void SetTab(int index)
    {
        // 切页签时顺手关掉应用库浮层，避免"页签变了、内容还是应用库"
        if (PickerOverlay.Visibility == Visibility.Visible) PickerOverlay.Visibility = Visibility.Collapsed;

        _tab = index;
        PaneFav.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaneClip.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PaneTodo.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 剪贴板页自己有「清空」了，顶部那个通用按钮只在待办页出现
        BtnAction.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        BtnAction.Content = "清除已完成";

        if (index == 1 && _clipsDirty) RebuildClips();
        RefreshTabVisual();
    }

    private void RefreshTabVisual()
    {
        var tabs = new[] { TabFav, TabClip, TabTodo };
        var selBg = (Brush)FindResource("SelFillBrush");
        var selFg = (Brush)FindResource("SelFgBrush");
        var dimFg = (Brush)FindResource("FgDimBrush");
        var accent = (Brush)FindResource("AccentBrush");

        for (int i = 0; i < tabs.Length; i++)
        {
            var selected = i == _tab;
            tabs[i].Background = selected ? selBg : Brushes.Transparent;
            tabs[i].Foreground = selected ? selFg : dimFg;
            tabs[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        var clipCount = ClipStore.Items.Count;
        TabClip.Content = clipCount > 0 ? $"剪贴板 ·{clipCount}" : "剪贴板";
        TabClip.Foreground = clipCount > 0 && _tab != 1 ? accent : TabClip.Foreground;
    }

    private void BtnAction_Click(object sender, RoutedEventArgs e)
    {
        if (_tab == 2)
        {
            var done = _todos.Where(t => t.Done).ToList();
            foreach (var d in done) { _todos.Remove(d); Store.Config.Todos.Remove(d); }
            if (done.Count > 0) { Store.Save(); _scheduler.Refresh(_todos); }
            UpdateTodoFooter();
        }
    }

    // ===================== 收藏 =====================

    private void RebuildFavorites()
    {
        _fileFavs.Clear();
        _appFavs.Clear();
        foreach (var f in Store.Config.Favorites)
        {
            if (f.Kind == FavKind.App) _appFavs.Add(f);
            else _fileFavs.Add(f);
        }

        FileHeader.Text = _fileFavs.Count > 0 ? $"文件 · {_fileFavs.Count}" : "文件";
        AppHeader.Text = _appFavs.Count > 0 ? $"应用 · {_appFavs.Count}" : "应用";
        FileEmpty.Visibility = _fileFavs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppEmpty.Visibility = _appFavs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool AddFavorite(string path, bool asApp)
    {
        path = path.Trim().TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Store.Config.Favorites.Any(f => string.Equals(f.Target, path, StringComparison.OrdinalIgnoreCase)))
            return false;

        var item = FavoriteItem.FromPath(path, asApp);
        Store.Config.Favorites.Add(item);
        if (!_previewMode) Store.Save();
        RebuildFavorites();
        return true;
    }

    private void AddPaths(IEnumerable<string> paths, bool asApp)
    {
        foreach (var p in paths) AddFavorite(p, asApp);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "选择要收藏的文件" };
            if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames, false);
        }
        finally { _dialogOpen = false; }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "选择要收藏的文件夹" };
            if (dlg.ShowDialog(this) == true) AddPaths(dlg.FolderNames, false);
        }
        finally { _dialogOpen = false; }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is FavoriteItem item) Launcher.Open(item);
    }

    private void TileMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is FavoriteItem item) Launcher.Open(item);
    }

    private void TileMenuReveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is FavoriteItem item) Launcher.RevealInExplorer(item.Target);
    }

    private void TileMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FavoriteItem item) return;
        Store.Config.Favorites.Remove(item);
        Store.Save();
        RebuildFavorites();
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (!_expanded) Expand(false, "drag");
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddPaths(paths, false);
            FlashHint($"已收藏 {paths.Length} 项");
        }
        e.Handled = true;
    }

    /// <summary>
    /// 瞬时提示。速览页有自己的提示条，剪贴板页也放了一条——
    /// 以前 FlashHint 只写 FavHint，在剪贴板页点「清空 / 固定」时用户根本看不到反馈。
    /// </summary>
    private void FlashHint(string text)
    {
        if (FavHint != null) FavHint.Text = text;

        if (ClipHint != null && ClipHintBox != null)
        {
            ClipHint.Text = text;
            ClipHintBox.Visibility = Visibility.Visible;

            _hintTimer?.Stop();
            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            _hintTimer.Tick += (_, _) =>
            {
                _hintTimer.Stop();
                ClipHintBox.Visibility = Visibility.Collapsed;
            };
            _hintTimer.Start();
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (FavHint != null) FavHint.Text = "把文件、文件夹或 exe 直接拖到刘海里即可收藏";
        };
        timer.Start();
    }

    // ===================== 应用库 =====================

    private Task<List<FavoriteItem>>? _appScanTask;
    private Task? _iconWarmTask;

    /// <summary>后台扫描开始菜单（实测 93 个应用约 40ms）。重复调用返回同一个任务。</summary>
    private Task<List<FavoriteItem>> ScanAppsAsync()
    {
        _appScanTask ??= Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var apps = AppIndexer.GetInstalledApps();
            Log.Write($"picker: 扫描 {apps.Count} 个应用，耗时 {sw.ElapsedMilliseconds}ms（后台线程）");
            return apps;
        });
        return _appScanTask;
    }

    /// <summary>
    /// 后台预热图标。SHGetFileInfo 每个要约 30ms，93 个就是 3 秒多——
    /// 这是以前点「＋ 应用」卡顿的真正大头，绝不能放在 UI 线程上。
    /// </summary>
    private Task WarmIconsAsync(List<FavoriteItem> apps)
    {
        _iconWarmTask ??= Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            IconService.Prewarm(apps.Select(a => a.Target));
            Log.Write($"picker: 图标预热 {apps.Count} 个，耗时 {sw.ElapsedMilliseconds}ms（后台线程）");
        });
        return _iconWarmTask;
    }

    /// <summary>等后台索引完成后填充列表；中途关掉浮层就不填了</summary>
    private async Task WarmAndShowPickerAsync()
    {
        List<FavoriteItem> apps;
        try
        {
            apps = await ScanAppsAsync();
        }
        catch (Exception ex)
        {
            Log.Ex("扫描已安装应用失败", ex);
            PickerLoading.Text = "扫描失败，请重试";
            return;
        }

        // 列表先出来（扫描只要几十毫秒），可见项的图标按需懒加载
        if (PickerOverlay.Visibility == Visibility.Visible)
        {
            ShowPicker(PickerSearch.Text);
            PickerLoading.Visibility = Visibility.Collapsed;
        }

        try
        {
            await WarmIconsAsync(apps);

            // 图标缓存就绪后刷新一次：容器重建时全部命中缓存，不会再一个个闪
            if (PickerOverlay.Visibility == Visibility.Visible)
                ShowPicker(PickerSearch.Text);
        }
        catch (Exception ex) { Log.Ex("图标预热失败", ex); }
    }

    /// <summary>
    /// 「＋ 应用」：点开的这一帧只做"显示浮层 + 出占位提示"，
    /// 索引和图标预热全部在后台线程跑，UI 全程不卡。
    /// </summary>
    private void OpenPicker_Click(object sender, RoutedEventArgs e)
    {
        PickerOverlay.Visibility = Visibility.Visible;
        PickerSearch.Text = "";
        PickerHint.Visibility = Visibility.Visible;
        PickerLoading.Text = "正在扫描已安装应用…";
        PickerLoading.Visibility = Visibility.Visible;
        _pickerItems.Clear();
        PickerSearch.Focus();

        _ = WarmAndShowPickerAsync();
    }

    private void ClosePicker_Click(object sender, RoutedEventArgs e) => ClosePicker();

    private void ClosePicker()
    {
        PickerOverlay.Visibility = Visibility.Collapsed;
        PickerLoading.Visibility = Visibility.Collapsed;
        PickerSearch.Text = "";
    }

    /// <summary>搜索框防抖：连续敲键盘时每 140ms 才过滤一次，别每个字符都重建列表</summary>
    private void PickerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        PickerHint.Visibility = string.IsNullOrEmpty(PickerSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_pickerDebounce == null) return;
        _pickerDebounce.Stop();
        _pickerDebounce.Start();
    }

    private void PickerDebounce_Tick(object? sender, EventArgs e)
    {
        _pickerDebounce?.Stop();
        if (PickerOverlay.Visibility != Visibility.Visible) return;
        // 后台扫描还没跑完就先别筛——好了之后会按当前关键词统一刷新
        if (_appScanTask is { IsCompleted: false }) return;
        ShowPicker(PickerSearch.Text);
    }

    private void ShowPicker(string? filter)
    {
        _pickerItems.Clear();
        var taken = new HashSet<string>(Store.Config.Favorites.Select(f => f.Target), StringComparer.OrdinalIgnoreCase);
        var all = AppIndexer.GetInstalledApps();
        foreach (var a in all)
        {
            if (!string.IsNullOrWhiteSpace(filter) &&
                !a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            a.IsAdded = taken.Contains(a.Target);
            _pickerItems.Add(a);
            if (_pickerItems.Count >= 300) break;
        }
        PickerHint.Visibility = string.IsNullOrEmpty(filter) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PickerItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not FavoriteItem item) return;

        if (item.IsAdded)
        {
            FlashHint($"{item.Name} 已经在收藏里了");
            return;
        }

        AddFavorite(item.Target, true);
        item.IsAdded = true;
        FlashHint($"已添加 {item.Name}");
    }

    public void ReloadApps()
    {
        AppIndexer.ClearCache();
        _appScanTask = null;        // 索引作废，下次打开重新扫
        _iconWarmTask = null;
        FlashHint("应用列表已重新扫描");

        if (PickerOverlay.Visibility == Visibility.Visible)
        {
            PickerLoading.Text = "正在重新扫描…";
            PickerLoading.Visibility = Visibility.Visible;
            _pickerItems.Clear();
            _ = WarmAndShowPickerAsync();
        }
    }

    // ===================== 剪贴板历史 =====================

    private void OnClipCaptured(ClipItem item)
    {
        ClipStore.Add(item);
        _clipsDirty = true;
        RefreshTabVisual();
        if (_tab == 1)
        {
            // 收起状态下也顺手重建：这时没有动画在跑，成本落在后台，
            // 等用户展开时列表已经是新的（展开那一帧零成本）。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RebuildClips();
            Log.Write($"clip: 后台重建 {_clipItems.Count} 条，耗时 {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>按当前搜索词 + 分类重建列表</summary>
    private void RebuildClips()
    {
        var q = (ClipSearch?.Text ?? "").Trim();
        _clipItems.Clear();

        foreach (var it in ClipStore.Items)
        {
            if (!MatchesFilter(it)) continue;
            if (q.Length > 0 && !MatchesQuery(it, q)) continue;
            _clipItems.Add(it);
        }

        _clipsDirty = false;
        ClipEmpty.Visibility = _clipItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmpty.Text = ClipStore.Items.Count == 0
            ? "还没有复制记录，复制点什么试试"
            : "没有匹配的记录";
    }

    private bool MatchesFilter(ClipItem it) => _clipFilter switch
    {
        "text" => it.Kind == ClipKind.Text,
        "image" => it.Kind == ClipKind.Image,
        "file" => it.Kind == ClipKind.File,
        "url" => it.Kind == ClipKind.Url,
        "code" => it.Kind == ClipKind.Code,
        _ => true
    };

    private static bool MatchesQuery(ClipItem it, string q)
    {
        if (it.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (it.Source?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
        return it.Files.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void ClipSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClipSearchHint.Visibility = string.IsNullOrEmpty(ClipSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RebuildClips();
    }

    private void ClipFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        _clipFilter = b.Tag?.ToString() ?? "all";
        RefreshClipFilterVisual();
        RebuildClips();
    }

    private static readonly string[] ClipFilterTags = { "all", "text", "image", "file", "url", "code" };

    private void RefreshClipFilterVisual()
    {
        var selBg = (Brush)FindResource("ChipSelBgBrush");
        var selFg = (Brush)FindResource("ChipSelFgBrush");
        var idleBg = (Brush)FindResource("ChipBgBrush");
        var idleFg = (Brush)FindResource("ChipFgBrush");

        foreach (var b in FindChips())
        {
            var on = (b.Tag?.ToString() ?? "all") == _clipFilter;
            b.Background = on ? selBg : idleBg;
            b.Foreground = on ? selFg : idleFg;
            b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private List<Button> FindChips()
    {
        var list = new List<Button>();
        void Walk(DependencyObject o)
        {
            foreach (var c in LogicalTreeHelper.GetChildren(o))
            {
                if (c is Button b && b.Tag is string t && ClipFilterTags.Contains(t)) list.Add(b);
                if (c is DependencyObject d) Walk(d);
            }
        }
        Walk(PaneClip);
        return list;
    }

    private void ClipCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is ClipItem it) CopyClipToClipboard(it);
    }

    /// <summary>只写剪贴板，不粘贴（「复制」按钮走这条路）</summary>
    private void CopyClipToClipboard(ClipItem it)
    {
        if (!WriteClipboard(it)) return;
        if (_pinned) { _pinned = false; Collapse("copied"); }
        FlashHint("已复制到剪贴板");
    }

    /// <summary>
    /// 点卡片：写入剪贴板 + 收起刘海 + 把 Ctrl+V 送到刚才那个窗口。
    /// 这是仿 TieZ 的关键交互——不用再切回去手动粘贴。
    /// </summary>
    private async void ClipCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not ClipItem it) return;

        if (!WriteClipboard(it)) return;

        // 先收起：顺带卸掉"点外部收起"的全局钩子，免得后面发键被自己拦住
        _pinned = false;
        Collapse("paste");
        await PasteAsync();
    }

    private async Task PasteAsync()
    {
        try
        {
            var ok = await _focus.PasteAsync();
            if (ok)
            {
                Log.Write("paste: 已发送 Ctrl+V");
            }
            else
            {
                Log.Write("paste: 没有可用的目标窗口，只做了复制");
                (Application.Current as App)?.ShowBalloon("已复制到剪贴板",
                    "没找到刚才的输入位置，请手动 Ctrl+V 粘贴。");
            }
        }
        catch (Exception ex)
        {
            Log.Ex("自动粘贴失败", ex);
        }
    }

    /// <summary>把一条记录写进系统剪贴板；失败时给提示并返回 false</summary>
    private bool WriteClipboard(ClipItem it)
    {
        try
        {
            _clipWatcher.MarkSelfWrite();

            switch (it.Kind)
            {
                case ClipKind.Image when it.ImageFile != null:
                {
                    var path = ClipStore.ImagePathOf(it.ImageFile);
                    if (!File.Exists(path)) { FlashHint("图片文件已不存在"); return false; }

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    Clipboard.SetImage(bmp);
                    return true;
                }
                case ClipKind.File when it.Files.Count > 0:
                {
                    var data = new DataObject();
                    var list = new System.Collections.Specialized.StringCollection();
                    foreach (var f in it.Files) list.Add(f);
                    data.SetFileDropList(list);
                    Clipboard.SetDataObject(data, true);
                    return true;
                }
                default:
                    Clipboard.SetText(it.Text ?? "");
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Ex("写剪贴板失败", ex);
            FlashHint("复制失败：剪贴板被占用");
            return false;
        }
    }

    private void ClipPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ClipItem it) return;
        ClipStore.TogglePin(it);
        FlashHint(it.Pinned ? "已固定，不会被 50 条上限挤掉" : "已取消固定");
    }

    private void ClipDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ClipItem it) return;
        ClipStore.Remove(it);
        _clipItems.Remove(it);
        RefreshTabVisual();
        ClipEmpty.Visibility = _clipItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClipClear_Click(object sender, RoutedEventArgs e)
    {
        ClipStore.Clear(keepPinned: true);
        RebuildClips();
        RefreshTabVisual();
        FlashHint("已清空剪贴板历史（固定项保留）");
    }

    // ===================== 待办 =====================

    private void ResetTodoInputs()
    {
        var due = RoundUp(DateTime.Now.AddHours(1), 5);
        TodoDue.Text = due.ToString("MM-dd HH:mm");
        TodoBefore.Text = "15";
        UpdateTitleHint();
    }

    private static DateTime RoundUp(DateTime t, int minutes)
    {
        var m = t.Minute;
        var add = (minutes - m % minutes) % minutes;
        return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0).AddMinutes(add);
    }

    private void QuickBefore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b) TodoBefore.Text = b.Tag?.ToString() ?? "15";
    }

    private void AddTodo_Click(object sender, RoutedEventArgs e)
    {
        var title = TodoTitle.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            TodoTitle.Focus();
            FlashFooter("先写一句要做的事");
            return;
        }

        if (!TryParseDue(TodoDue.Text, out var due))
            due = DateTime.Now.AddHours(1);
        if (!int.TryParse(TodoBefore.Text, out var before)) before = 15;
        before = Math.Clamp(before, 0, 60 * 24 * 30);

        var item = new TodoItem { Title = title, Due = due, BeforeMinutes = before };
        _todos.Insert(0, item);
        Store.Config.Todos.Insert(0, item);
        Store.Save();

        TodoTitle.Text = "";
        UpdateTitleHint();
        _scheduler.Refresh(_todos);
        _scheduler.Sweep();
        UpdateTodoFooter();
    }

    private static bool TryParseDue(string text, out DateTime due)
    {
        due = default;
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return false;

        var now = DateTime.Now;
        var formats = new[]
        {
            "MM-dd HH:mm", "M-d HH:mm", "MM-dd H:mm", "yyyy-MM-dd HH:mm", "yyyy-M-d HH:mm",
            "MM/dd HH:mm", "yyyy/MM/dd HH:mm", "HH:mm", "H:mm"
        };

        if (DateTime.TryParseExact(text, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            if (parsed.Year < 2000) parsed = new DateTime(now.Year, parsed.Month == 1 && parsed.Day == 1 ? now.Month : parsed.Month,
                parsed.Month == 1 && parsed.Day == 1 ? now.Day : parsed.Day, parsed.Hour, parsed.Minute, 0);
            due = parsed;
            return true;
        }

        if (DateTime.TryParse(text, out var loose)) { due = loose; return true; }
        return false;
    }

    private void TodoCheck_Click(object sender, RoutedEventArgs e)
    {
        Store.Save();
        _scheduler.Refresh(_todos);
        UpdateTodoFooter();
    }

    private void TodoDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item) return;
        _todos.Remove(item);
        Store.Config.Todos.Remove(item);
        Store.Save();
        _scheduler.Refresh(_todos);
        UpdateTodoFooter();
    }

    private void TodoSnooze_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item) return;
        if (sender is not UIElement anchor) return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        foreach (var (label, minutes) in new[] { ("5 分钟后", 5), ("10 分钟后", 10), ("30 分钟后", 30), ("1 小时后", 60) })
        {
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) =>
            {
                TodoScheduler.Snooze(item, minutes);
                Store.Save();
                _scheduler.Refresh(_todos);
                _scheduler.Sweep();
                UpdateTodoFooter();
            };
            menu.Items.Add(mi);
        }

        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }

    private void OnTodoFired(TodoItem item)
    {
        Store.Save();
        if (Store.Config.PlaySound)
        {
            try { SystemSounds.Exclamation.Play(); } catch { }
        }

        _alertMode = true;          // 提醒态：鼠标不在刘海上也保持 8 秒
        _pinned = false;
        Expand(false, "alert");
        SetTab(2);
        FlashFooter($"⏰ 提醒：{item.Title}  ·  {item.DueText} 截止");
        (Application.Current as App)?.ShowBalloon("NotchDock 待办提醒", item.Title);

        _autoCollapse.Stop();
        _autoCollapse.Start();
    }

    private void FlashFooter(string text)
    {
        TodoFooter.Text = text;
        TodoFooter.Foreground = (Brush)FindResource("AccentBrush");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); UpdateTodoFooter(); };
        timer.Start();
    }

    private void UpdateTodoFooter()
    {
        TodoFooter.Foreground = (Brush)FindResource("FgMuteBrush");
        var pending = _todos.Count(t => !t.Done);
        var next = _todos.Where(t => !t.Done).OrderBy(t => t.FireAt).FirstOrDefault();
        TodoFooter.Text = pending == 0
            ? "没有进行中的待办"
            : next == null ? $"{pending} 项待办" : $"{pending} 项待办 · 最近一条 {next.FireAt:HH:mm} 提醒";
        TodoEmpty.Visibility = _todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTitleHint()
    {
        TodoTitleHint.Visibility = string.IsNullOrEmpty(TodoTitle.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== 菜单 =====================
    // （刘海里的「⋯」入口已移除：设置统一走右上角齿轮，功能都收在设置面板里；
    //   托盘右键菜单仍由 AppMenu.Build 这一份定义渲染，内容与设置面板保持一致。）

    /// <summary>供菜单模型调用：切换开机自启并给出提示</summary>
    public void SetAutoStart(bool on)
    {
        AutoStart.Set(on);
        NotifyAutoStartChanged(AutoStart.IsEnabled());
    }

    public void NotifyAutoStartChanged(bool enabled)
        => FlashHint(enabled ? "已设置为开机自动启动" : "已取消开机自动启动");

    public void FlashFooterPublic(string text) => FlashFooter(text);
}
