using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace NotchDock;

/// <summary>
/// 开发期预览：把窗口内容直接渲染成 PNG（不依赖屏幕截图），
/// 启动参数 --render &lt;目录&gt; 触发。
/// </summary>
public partial class NotchWindow
{
    public void RenderPreview(string dir, string theme = "black")
    {
        Directory.CreateDirectory(dir);
        _previewMode = true;

        Theme.Apply(theme);
        Store.Config.Theme = Theme.Normalize(theme);
        SyncGlassLayer();

        // 折叠态：窗口宽 660、高 30，刘海 214 居中，只截刘海那块
        SavePng(Path.Combine(dir, "0-collapsed.png"),
                new Rect(Math.Round((ExpW - CollapsedWidth) / 2), 0,
                         (int)CollapsedWidth, (int)CollapsedHeight));

        var before = Store.Config.Favorites.Count;
        SeedDemoContent();

        Expand(true);
        ForceExpandedVisual();
        SetTab(0);
        UpdateLayout();
        SavePng(Path.Combine(dir, "1-favorites.png"));

        SetTab(1);
        UpdateLayout();
        SavePng(Path.Combine(dir, "2-clipboard.png"));

        SetTab(2);
        UpdateLayout();
        SavePng(Path.Combine(dir, "3-todos.png"));

        PickerOverlay.Visibility = Visibility.Visible;
        ShowPicker(null);
        UpdateLayout();
        SavePng(Path.Combine(dir, "4-apppicker.png"));

        PickerOverlay.Visibility = Visibility.Collapsed;
        SettingsOverlay.Visibility = Visibility.Visible;
        RefreshHotKeyUi();
        UpdateLayout();
        SavePng(Path.Combine(dir, "5-settings.png"));
        SettingsOverlay.Visibility = Visibility.Collapsed;

        // 预览期间塞进去的演示磁贴不落盘
        while (Store.Config.Favorites.Count > before)
            Store.Config.Favorites.RemoveAt(Store.Config.Favorites.Count - 1);
        _previewMode = false;
    }

    /// <summary>跳过动画，直接把刘海摆成完全展开的样子（渲染预览用）</summary>
    private void ForceExpandedVisual()
    {
        _animToken++;
        StopShellAnim(false);

        var w = Math.Min(Store.Config.ExpandedWidth, ExpW);
        var h = Math.Min(Store.Config.ExpandedHeight, ExpH);

        Shell.Width = w;
        Height = h;
        Shell.CornerRadius = new CornerRadius(0, 0, CornerRadiusValue, CornerRadiusValue);

        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Visibility = Visibility.Visible;
        ContentHost.IsHitTestVisible = true;
        ContentHost.Opacity = 1;

        ContentShift.BeginAnimation(TranslateTransform.YProperty, null);
        ContentShift.Y = 0;

        CollapsedLayer.BeginAnimation(OpacityProperty, null);
        CollapsedLayer.Opacity = 0;

        Root.UpdateLayout();
    }

    /// <summary>把展开动画按进度拆成若干帧渲染出来，用于检查过渡中间态</summary>
    public void RenderAnimation(string dir)
    {
        Directory.CreateDirectory(dir);
        _previewMode = true;
        SeedDemoContent();
        SetTab(0);

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        const int steps = 12;

        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double et = ease.Ease(t);

            double w = CollapsedWidth + (ExpW - CollapsedWidth) * et;
            double h = CollapsedHeight + (ExpH - CollapsedHeight) * et;

            Shell.Width = w;
            Height = h;

            // 内容：延迟 40ms、时长 210ms，总时长按 380ms 折算到进度 t
            double ct = Math.Clamp((t * 340 - 40) / 210.0, 0, 1);
            ContentHost.BeginAnimation(OpacityProperty, null);
            ContentHost.Visibility = Visibility.Visible;
            ContentHost.Opacity = ct;

            // 下落：延迟 30ms、时长 300ms，从 -14 到 0
            double st = Math.Clamp((t * 340 - 30) / 300.0, 0, 1);
            ContentShift.BeginAnimation(TranslateTransform.YProperty, null);
            ContentShift.Y = -14 * (1 - st) * (1 - st);   // 近似 CubicEase.EaseOut 的反向

            CollapsedLayer.BeginAnimation(OpacityProperty, null);
            CollapsedLayer.Opacity = 1 - Math.Clamp(t * 340 / 110.0, 0, 1);

            Root.UpdateLayout();
            SavePng(Path.Combine(dir, $"anim-{i:00}.png"), new Rect(0, 0, ExpW, ExpH), 1);
        }

        _previewMode = false;
    }

    private void SavePng(string path, Rect? region = null, double scale = 2)
    {
        var r = region ?? new Rect(0, 0, Width, Height);

        // 图片缩略图是延迟解码的，只等到 Render 优先级还不够，
        // 要等到 ApplicationIdle（此时绑定、布局、图像解码都已完成）再截图
        Root.UpdateLayout();
        Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Root.UpdateLayout();
        Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(r.Width * scale),
                                         (int)Math.Ceiling(r.Height * scale),
                                         96 * scale, 96 * scale, PixelFormats.Pbgra32);

        // 整窗截图直接 Render(Root)：VisualBrush 那条路会把 Image 控件漏掉（缩略图渲染不出来）
        var full = Math.Abs(r.X) < 0.5 && Math.Abs(r.Y) < 0.5 &&
                   Math.Abs(r.Width - Width) < 0.5 && Math.Abs(r.Height - Height) < 0.5;

        if (full)
        {
            rtb.Render(Root);
        }
        else
        {
            var vb = new VisualBrush(Root)
            {
                Viewbox = r,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, r.Width, r.Height),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(vb, null, new Rect(0, 0, r.Width, r.Height));
            rtb.Render(dv);
        }

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Log.Write($"render -> {path} ({r.Width:0}x{r.Height:0})");
    }

    private void SeedDemoContent()
    {
        // 仅预览用：挑几个真实存在的路径做磁贴
        var samples = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            @"C:\Windows\System32\notepad.exe",
            @"C:\Windows\System32\calc.exe",
            @"C:\Windows\System32\mspaint.exe",
        };
        foreach (var s in samples)
            if (Directory.Exists(s) || File.Exists(s)) AddFavorite(s, false);

        if (_todos.Count == 0)
        {
            _todos.Add(new TodoItem { Title = "给王总回电话确认面试时间", Due = DateTime.Today.AddHours(15).AddMinutes(30), BeforeMinutes = 30 });
            _todos.Add(new TodoItem { Title = "整理临沂项目候选人推荐报告", Due = DateTime.Today.AddHours(18), BeforeMinutes = 60 });
            _todos.Add(new TodoItem { Title = "同步滴滴自驾司机面试名单", Due = DateTime.Today.AddDays(1).AddHours(9), BeforeMinutes = 1440, Done = true });
        }

        UpdateTodoFooter();
    }
}
