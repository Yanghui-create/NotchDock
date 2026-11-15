using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace NotchDock;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll()
    {
        foreach (var p in GetType().GetProperties())
            OnPropertyChanged(p.Name);
    }
}

public enum FavKind { File, Folder, App }

public sealed class FavoriteItem : ObservableObject
{
    private string _name = "";
    private string _target = "";
    private FavKind _kind;
    private DateTime _added = DateTime.Now;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public FavKind Kind { get => _kind; set => Set(ref _kind, value); }
    public DateTime Added { get => _added; set => Set(ref _added, value); }

    private bool _isAdded;
    /// <summary>应用库里用来标记"这个应用已经收藏过了"（不落盘）</summary>
    [JsonIgnore] public bool IsAdded { get => _isAdded; set => Set(ref _isAdded, value); }

    [JsonIgnore] public bool IsApp => Kind == FavKind.App;
    [JsonIgnore] public bool Exists => Kind == FavKind.Folder ? Directory.Exists(Target) : File.Exists(Target);
    [JsonIgnore] public System.Windows.Media.ImageSource? Icon => IconService.Get(Target);

    public static FavoriteItem FromPath(string path, bool preferApp = true)
    {
        var name = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) name = path;
        FavKind kind;
        if (Directory.Exists(path)) kind = FavKind.Folder;
        else
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            kind = preferApp && (ext == ".exe" || ext == ".lnk" || ext == ".url" || ext == ".bat" || ext == ".cmd")
                ? FavKind.App
                : FavKind.File;
        }
        return new FavoriteItem { Name = name, Target = path, Kind = kind };
    }
}

public sealed class TodoItem : ObservableObject
{
    private string _title = "";
    private DateTime _due = DateTime.Now.AddHours(1);
    private int _beforeMinutes = 15;
    private bool _done;
    private bool _fired;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get => _title; set { Set(ref _title, value); RaiseAll(); } }
    public DateTime Due { get => _due; set { Set(ref _due, value); RaiseAll(); } }
    public int BeforeMinutes { get => _beforeMinutes; set { Set(ref _beforeMinutes, value); RaiseAll(); } }
    public bool Done { get => _done; set { Set(ref _done, value); RaiseAll(); } }
    public bool Fired { get => _fired; set { Set(ref _fired, value); RaiseAll(); } }

    /// <summary>已到提醒时刻（截止时间 - 提前量）</summary>
    [JsonIgnore] public DateTime FireAt => Due.AddMinutes(-BeforeMinutes);

    [JsonIgnore] public string BeforeText => BeforeMinutes <= 0
        ? "准时"
        : BeforeMinutes < 60 ? $"提前{BeforeMinutes}分"
        : BeforeMinutes % 1440 == 0 ? $"提前{BeforeMinutes / 1440}天"
        : BeforeMinutes % 60 == 0 ? $"提前{BeforeMinutes / 60}小时"
        : $"提前{BeforeMinutes}分";

    [JsonIgnore] public string DueText => Due.Date == DateTime.Today
        ? Due.ToString("HH:mm")
        : Due.Year == DateTime.Today.Year ? Due.ToString("MM-dd HH:mm") : Due.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore] public string StatusText
    {
        get
        {
            if (Done) return "已完成";
            var span = FireAt - DateTime.Now;
            if (span.TotalMinutes <= 0) return Fired ? "已提醒" : "待提醒";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟后提醒";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时后提醒";
            return $"{(int)span.TotalDays} 天后提醒";
        }
    }

    [JsonIgnore] public bool IsOverdue => !Done && Due < DateTime.Now;

    public void Touch() => RaiseAll();
}

public sealed class AppConfig
{
    public List<FavoriteItem> Favorites { get; set; } = new();
    public List<TodoItem> Todos { get; set; } = new();

    public double ExpandedWidth { get; set; } = 660;
    public double ExpandedHeight { get; set; } = 520;
    public bool PlaySound { get; set; } = true;
    public bool ShowBadge { get; set; } = true;
    public bool CollapseOnBlur { get; set; } = true;
    public string SortMode { get; set; } = "manual";

    /// <summary>外观：black / white / glass</summary>
    public string Theme { get; set; } = "black";

    /// <summary>呼出剪贴板面板的全局热键，例如 "Win+V"</summary>
    public string HotKey { get; set; } = "Win+V";

    /// <summary>是否启用全局热键</summary>
    public bool HotKeyEnabled { get; set; } = true;
}
