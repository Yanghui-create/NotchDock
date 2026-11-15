using System.Windows;
using System.Windows.Controls;
using WF = System.Windows.Forms;

namespace NotchDock;

public enum MenuNodeKind { Item, Separator }

/// <summary>
/// 菜单的声明式模型。
///
/// 早期托盘菜单（WinForms ContextMenuStrip）和刘海里的「⋯」菜单（WPF ContextMenu）
/// 是两段各写一遍的代码，结果托盘里有「外观」，⋯ 里没有——两边慢慢就漂移了。
/// 后来刘海里的「⋯」入口被取消（设置统一走右上角齿轮，功能收进设置面板），
/// 托盘菜单仍由这一份定义渲染；设置面板里的「外观 / 开机自启 / 重新扫描 / 数据目录 / 退出」
/// 与这里的项保持一致。
/// </summary>
public sealed class MenuNode
{
    public MenuNodeKind Kind { get; private init; } = MenuNodeKind.Item;
    public string Label { get; private init; } = "";
    public string? Id { get; private init; }
    public bool Checkable { get; private init; }
    public Func<bool>? IsChecked { get; private init; }
    public Action? Run { get; private init; }
    public List<MenuNode> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

    public static MenuNode Separator() => new() { Kind = MenuNodeKind.Separator };

    public static MenuNode Item(string label, Action run, string? id = null) =>
        new() { Label = label, Run = run, Id = id };

    /// <summary>可勾选项（像「开机自动启动」）</summary>
    public static MenuNode Toggle(string label, Func<bool> isChecked, Action run, string? id = null) =>
        new() { Label = label, IsChecked = isChecked, Run = run, Checkable = true, Id = id };

    /// <summary>带子菜单的项（像「外观」）</summary>
    public static MenuNode Sub(string label, params MenuNode[] children)
    {
        var n = new MenuNode { Label = label };
        n.Children.AddRange(children);
        return n;
    }
}

public static class AppMenu
{
    /// <summary>唯一的菜单定义。托盘与刘海「⋯」都渲染它。</summary>
    public static List<MenuNode> Build(App app)
    {
        var win = app.Window;

        var themeItems = Theme.All
            .Select(kind => MenuNode.Toggle(
                Theme.DisplayName(kind),
                () => Theme.Normalize(win?.CurrentTheme ?? Store.Config.Theme) == kind,
                () => win?.ApplyTheme(kind),
                "theme:" + kind))
            .ToArray();

        return new List<MenuNode>
        {
            MenuNode.Item("展开 / 收起刘海", () => win?.ToggleByUser()),

            MenuNode.Separator(),

            MenuNode.Toggle("开机自动启动",
                () => AutoStart.IsEnabled(),
                () => win?.SetAutoStart(!AutoStart.IsEnabled()),
                "autostart"),

            MenuNode.Sub("外观", themeItems),

            MenuNode.Item("设置…", () => win?.ShowSettings()),

            MenuNode.Separator(),

            MenuNode.Item("重新扫描已安装应用", () =>
            {
                AppIndexer.ClearCache();
                win?.ReloadApps();
            }),

            MenuNode.Item("打开数据目录", () => Launcher.OpenPath(Store.Dir, true)),

            MenuNode.Separator(),

            MenuNode.Item("退出 NotchDock", () => app.ExitApp())
        };
    }

    // ===================== 渲染器一：WPF ContextMenu（刘海里的「⋯」） =====================

    public static ContextMenu RenderWpf(IEnumerable<MenuNode> nodes)
    {
        var menu = new ContextMenu();
        foreach (var n in nodes) menu.Items.Add(RenderWpfNode(n));
        return menu;
    }

    private static object RenderWpfNode(MenuNode n)
    {
        if (n.Kind == MenuNodeKind.Separator) return new Separator();

        var mi = new MenuItem { Header = n.Label };

        if (n.HasChildren)
        {
            foreach (var c in n.Children) mi.Items.Add(RenderWpfNode(c));
            return mi;
        }

        if (n.Checkable && n.IsChecked != null)
        {
            mi.IsCheckable = true;
            mi.IsChecked = n.IsChecked();
        }

        var run = n.Run;
        mi.Click += (_, _) => run?.Invoke();
        return mi;
    }

    // ===================== 渲染器二：WinForms 托盘菜单 =====================

    public static WF.ContextMenuStrip RenderTray(IEnumerable<MenuNode> nodes)
    {
        var strip = new WF.ContextMenuStrip { ShowImageMargin = false };
        var map = new List<(MenuNode node, WF.ToolStripMenuItem item)>();

        foreach (var n in nodes) AddTray(strip.Items, n, map);

        // 每次弹出前把勾选状态刷一遍（主题可能被 ⋯ 菜单改过）
        strip.Opening += (_, _) =>
        {
            foreach (var (node, item) in map)
                if (node.IsChecked != null) item.Checked = node.IsChecked();
        };

        return strip;
    }

    private static void AddTray(WF.ToolStripItemCollection target, MenuNode n,
                                List<(MenuNode, WF.ToolStripMenuItem)> map)
    {
        if (n.Kind == MenuNodeKind.Separator)
        {
            target.Add(new WF.ToolStripSeparator());
            return;
        }

        var item = new WF.ToolStripMenuItem(n.Label);

        if (n.HasChildren)
        {
            foreach (var c in n.Children) AddTray(item.DropDownItems, c, map);
        }
        else
        {
            var run = n.Run;
            item.Click += (_, _) => run?.Invoke();
        }

        target.Add(item);
        map.Add((n, item));
    }
}
