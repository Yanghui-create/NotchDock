using System.Windows;
using System.Windows.Media;

namespace NotchDock;

/// <summary>
/// 三套外观：黑色 / 白色 / 液态玻璃。
/// 做法是把 Application.Resources 里那批 Brush 整体换掉——所以界面里必须用
/// DynamicResource 引颜色，用 StaticResource 的话换主题不会刷新。
/// </summary>
public static class Theme
{
    public const string Black = "black";
    public const string White = "white";
    public const string Glass = "glass";

    /// <summary>选项顺序：托盘菜单、⋯ 菜单都按这个顺序排列</summary>
    public static readonly string[] All = { Black, White, Glass };

    public static string Normalize(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "white" or "light" => White,
        "glass" or "liquid" or "liquidglass" or "liquid-glass" => Glass,
        _ => Black
    };

    public static string DisplayName(string kind) => Normalize(kind) switch
    {
        White => "白色",
        Glass => "液态玻璃",
        _ => "黑色"
    };

    public static void Apply(string? kind)
    {
        var r = Application.Current?.Resources;
        if (r == null) return;

        switch (Normalize(kind))
        {
            case White: ApplyWhite(r); break;
            case Glass: ApplyGlass(r); break;
            default: ApplyBlack(r); break;
        }
    }

    // ===================== 黑色（macOS 深色系统色） =====================

    private static void ApplyBlack(ResourceDictionary r)
    {
        Set(r, "FgBrush", 0xFFEDEDF2);
        Set(r, "FgDimBrush", 0xFFA0A0AD);
        Set(r, "FgMuteBrush", 0xFF70707E);
        Set(r, "StrokeBrush", 0x24FFFFFF);

        Set(r, "AccentBrush", 0xFF0A84FF);          // macOS systemBlue (dark)
        Set(r, "AccentSoftBrush", 0x360A84FF);
        Set(r, "DangerBrush", 0xFFFF453A);          // systemRed
        Set(r, "SuccessBrush", 0xFF30D158);         // systemGreen

        Set(r, "CardBrush", 0x0FFFFFFF);
        Set(r, "CardHoverBrush", 0x1AFFFFFF);
        Set(r, "InputBrush", 0x14FFFFFF);
        Set(r, "InputHoverBrush", 0x1FFFFFFF);
        Set(r, "CheckBorderBrush", 0x4DFFFFFF);
        Set(r, "ScrollThumbBrush", 0x38FFFFFF);
        Set(r, "TickBrush", 0xFF0B1220);
        Set(r, "DotBrush", 0xFF23232B);
        Set(r, "DotSmallBrush", 0xFF3B3B46);
        Set(r, "BadgeTextBrush", 0xFFFFFFFF);
        Set(r, "OverlayBrush", 0xFF08080B);

        // 页签：选中项是一枚实心蓝胶囊（Finder / 系统设置侧栏那种），未选中只有文字
        Gradient(r, "SelFillBrush", (0xFF3E9BFF, 0), (0xFF0A6FF0, 1));
        Set(r, "SelFgBrush", 0xFFFFFFFF);
        Set(r, "PillHoverBrush", 0x1AFFFFFF);

        // 分类 chip：选中用蓝色淡染，未选中是极淡的填充
        Set(r, "ChipBgBrush", 0x14FFFFFF);
        Set(r, "ChipFgBrush", 0xFFB6B6C2);
        Set(r, "ChipSelBgBrush", 0x400A84FF);
        Set(r, "ChipSelFgBrush", 0xFF8AC2FF);

        // 小按钮（复制 / 固定 / 删 / ＋文件 …）
        Set(r, "BtnFillBrush", 0x17FFFFFF);
        Set(r, "BtnHoverBrush", 0x2BFFFFFF);
        Set(r, "BtnDangerHoverBrush", 0x40FF453A);

        // 二级菜单
        Set(r, "MenuBgBrush", 0xF51A1A1E);
        Set(r, "MenuBorderBrush", 0x2BFFFFFF);
        Set(r, "MenuFgBrush", 0xFFE9E9EF);
        Set(r, "MenuHoverBrush", 0xFF0A84FF);
        Set(r, "MenuHoverFgBrush", 0xFFFFFFFF);
        Set(r, "MenuSepBrush", 0x1FFFFFFF);

        Gradient(r, "ShellBgBrush", (0xFF121217, 0), (0xFF07070B, 0.55), (0xFF040407, 1));
    }

    // ===================== 白色（macOS 浅色系统色） =====================

    private static void ApplyWhite(ResourceDictionary r)
    {
        Set(r, "FgBrush", 0xFF16171B);
        Set(r, "FgDimBrush", 0xFF4C4E56);
        Set(r, "FgMuteBrush", 0xFF8A8C93);
        Set(r, "StrokeBrush", 0x14000000);

        Set(r, "AccentBrush", 0xFF007AFF);          // macOS systemBlue (light)
        Set(r, "AccentSoftBrush", 0x26007AFF);
        Set(r, "DangerBrush", 0xFFFF3B30);
        Set(r, "SuccessBrush", 0xFF34C759);

        Set(r, "CardBrush", 0xF2FFFFFF);            // 浅色下卡片是"白纸"，不是淡淡一层灰
        Set(r, "CardHoverBrush", 0xFFFFFFFF);
        Set(r, "InputBrush", 0x0D000000);
        Set(r, "InputHoverBrush", 0x16000000);
        Set(r, "CheckBorderBrush", 0x33000000);
        Set(r, "ScrollThumbBrush", 0x29000000);
        Set(r, "TickBrush", 0xFFFFFFFF);
        Set(r, "DotBrush", 0xFFD2D4DA);
        Set(r, "DotSmallBrush", 0xFFAFB2BA);
        Set(r, "BadgeTextBrush", 0xFFFFFFFF);
        Set(r, "OverlayBrush", 0xFFFFFFFF);

        Gradient(r, "SelFillBrush", (0xFF3A94FF, 0), (0xFF0064EA, 1));
        Set(r, "SelFgBrush", 0xFFFFFFFF);
        Set(r, "PillHoverBrush", 0x12000000);

        Set(r, "ChipBgBrush", 0x0D000000);
        Set(r, "ChipFgBrush", 0xFF55575F);
        Set(r, "ChipSelBgBrush", 0x2E007AFF);
        Set(r, "ChipSelFgBrush", 0xFF0066DB);

        Set(r, "BtnFillBrush", 0x0F000000);
        Set(r, "BtnHoverBrush", 0x1F000000);
        Set(r, "BtnDangerHoverBrush", 0x33FF3B30);

        Set(r, "MenuBgBrush", 0xFCFFFFFF);
        Set(r, "MenuBorderBrush", 0x1A000000);
        Set(r, "MenuFgBrush", 0xFF1B1C20);
        Set(r, "MenuHoverBrush", 0xFF007AFF);
        Set(r, "MenuHoverFgBrush", 0xFFFFFFFF);
        Set(r, "MenuSepBrush", 0x14000000);

        Gradient(r, "ShellBgBrush", (0xFFFFFFFF, 0), (0xFFF7F8FA, 0.55), (0xFFEFF1F5, 1));
    }

    // ===================== 液态玻璃（实心磨砂 + 蓝调高光） =====================

    private static void ApplyGlass(ResourceDictionary r)
    {
        Set(r, "FgBrush", 0xFF101218);
        Set(r, "FgDimBrush", 0xFF3E424B);
        Set(r, "FgMuteBrush", 0xFF70747E);
        Set(r, "StrokeBrush", 0x33000000);

        Set(r, "AccentBrush", 0xFF0A7CFF);
        Set(r, "AccentSoftBrush", 0x2E0A7CFF);
        Set(r, "DangerBrush", 0xFFD93B4B);
        Set(r, "SuccessBrush", 0xFF0B9E63);

        Set(r, "CardBrush", 0xB8FFFFFF);            // 玻璃板上的卡片略透，像贴在磨砂面上
        Set(r, "CardHoverBrush", 0xD9FFFFFF);
        Set(r, "InputBrush", 0x1A000000);
        Set(r, "InputHoverBrush", 0x26000000);
        Set(r, "CheckBorderBrush", 0x44000000);
        Set(r, "ScrollThumbBrush", 0x3D000000);
        Set(r, "TickBrush", 0xFFFFFFFF);
        Set(r, "DotBrush", 0xFF3A3E48);
        Set(r, "DotSmallBrush", 0xFF5A5F6A);
        Set(r, "BadgeTextBrush", 0xFFFFFFFF);
        Set(r, "OverlayBrush", 0xF7F5F8FC);

        Gradient(r, "SelFillBrush", (0xFF46A0FF, 0), (0xFF0067E4, 1));
        Set(r, "SelFgBrush", 0xFFFFFFFF);
        Set(r, "PillHoverBrush", 0x16000000);

        Set(r, "ChipBgBrush", 0x12000000);
        Set(r, "ChipFgBrush", 0xFF494D57);
        Set(r, "ChipSelBgBrush", 0x330A7CFF);
        Set(r, "ChipSelFgBrush", 0xFF0060CC);

        Set(r, "BtnFillBrush", 0x14000000);
        Set(r, "BtnHoverBrush", 0x24000000);
        Set(r, "BtnDangerHoverBrush", 0x33D93B4B);

        Set(r, "MenuBgBrush", 0xF8FBFDFF);
        Set(r, "MenuBorderBrush", 0x24000000);
        Set(r, "MenuFgBrush", 0xFF15171C);
        Set(r, "MenuHoverBrush", 0xFF0A7CFF);
        Set(r, "MenuHoverFgBrush", 0xFFFFFFFF);
        Set(r, "MenuSepBrush", 0x1A000000);

        // 磨砂玻璃主体。
        //
        // 硬约束：AllowsTransparency=true 的窗口走 UpdateLayeredWindow，WPF 拿不到
        // "窗口背后"的像素，做不了 backdrop blur。而没有模糊的半透明玻璃，只要
        // alpha 掉到 95% 以下，背后的文字就会和前景重影（实测背后的聊天窗口、
        // 网页能直接透上来，彻底没法读）。
        //
        // 所以这里走"实心磨砂"：完全不透明，但用冷调渐变 + 顶部弧光 + 内圈亮线
        // + 底部折射来做出玻璃的厚度与质感，而不是靠透明度。
        // 底色刻意不取纯白，否则顶部的纯白弧光在白底上根本看不见
        Gradient(r, "ShellBgBrush", (0xFFF5F8FF, 0), (0xFFEFF4FE, 0.45), (0xFFE6EDF9, 1));
    }

    // ===================== 工具 =====================

    private static void Set(ResourceDictionary r, string key, uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        r[key] = b;
    }

    private static void Gradient(ResourceDictionary r, string key, params (uint argb, double offset)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (argb, offset) in stops)
        {
            g.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb), offset));
        }
        g.Freeze();
        r[key] = g;
    }
}
