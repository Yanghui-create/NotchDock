using System.Drawing;
using System.Threading;
using System.Windows;
using DrawingIcon = System.Drawing.Icon;

namespace NotchDock;

public partial class App : Application
{
    private Mutex? _mutex;
    private System.Windows.Forms.NotifyIcon? _tray;
    private NotchWindow? _window;
    private DrawingIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "NotchDock.SingleInstance.v1", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Ex("UI 线程未处理异常", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex2) Log.Ex("后台未处理异常", ex2);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Ex("未观察任务异常", args.Exception);

        Store.Load();
        Theme.Apply(Store.Config.Theme);
        Log.Write($"[{DateTime.Now:HH:mm:ss}] NotchDock 启动");

        // 开发期：记录展开/收起动画的帧节奏（必须在窗口创建前设好，Loaded 里会用到）
        if (e.Args.Contains("--probe-anim")) NotchWindow.FrameProbeEnabled = true;

        // 开发期：模拟"托盘菜单里点了一次展开"，用来验证"点外部收起"
        var pinDemo = e.Args.Contains("--pin-demo");

        _window = new NotchWindow();
        _window.Show();

        if (pinDemo)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // 模拟"从托盘菜单展开"：窗口会被激活并获得键盘焦点
                _window?.Activate();
                _window?.ToggleByUser();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        CreateTray();

        // 开发期预览：NotchDock.exe --render <目录>
        var idx = Array.IndexOf(e.Args, "--render");
        if (idx >= 0 && e.Args.Length > idx + 1 && _window != null)
        {
            var dir = e.Args[idx + 1];
            var themeArg = e.Args.Length > idx + 2 ? e.Args[idx + 2] : "black";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _window.RenderPreview(dir, themeArg); }
                catch (Exception ex) { Log.Ex("渲染预览失败", ex); }
                ExitApp();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // 开发期动画拆帧：NotchDock.exe --anim <目录>
        var aidx = Array.IndexOf(e.Args, "--anim");
        if (aidx >= 0 && e.Args.Length > aidx + 1 && _window != null)
        {
            var dir = e.Args[aidx + 1];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _window.RenderAnimation(dir); }
                catch (Exception ex) { Log.Ex("渲染动画帧失败", ex); }
                ExitApp();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>菜单与托盘交互都从这里取窗口，菜单模型本身不依赖具体实例</summary>
    public NotchWindow? Window => _window;

    private void CreateTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon = BuildIcon(),
            Visible = true,
            Text = "NotchDock · 桌面刘海"
        };

        // 托盘菜单与刘海里的「⋯」菜单共用 AppMenu.Build 这一份定义，内容必然一致
        _tray.ContextMenuStrip = AppMenu.RenderTray(AppMenu.Build(this));
        _tray.DoubleClick += (_, _) => _window?.ToggleByUser();
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _tray?.ShowBalloonTip(2500, title, text, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
    }

    public void ExitApp()
    {
        try { _tray?.Dispose(); } catch { }
        _tray = null;
        _trayIcon?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _tray?.Dispose(); } catch { }
        base.OnExit(e);
    }

    /// <summary>
    /// 托盘图标直接取 exe 里嵌入的那张 Assets/NotchDock.ico，
    /// 保证「文件图标 / 任务栏 / Alt+Tab / 托盘」永远是同一个 logo。
    /// </summary>
    private static DrawingIcon BuildIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0)
            {
                using var src = DrawingIcon.ExtractAssociatedIcon(exe);
                if (src != null) return (DrawingIcon)src.Clone();
            }
        }
        catch (Exception ex) { Log.Ex("读取应用图标失败", ex); }

        return (DrawingIcon)SystemIcons.Application.Clone();
    }
}
