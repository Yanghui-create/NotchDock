using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchDock;

public enum ClipKind { Text, Image, File, Url, Code }

/// <summary>一条剪贴板历史。</summary>
public sealed class ClipItem : ObservableObject
{
    private bool _pinned;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; } = ClipKind.Text;
    public string Text { get; set; } = "";

    /// <summary>图片文件名（相对 clips 目录），文本类为 null</summary>
    public string? ImageFile { get; set; }

    /// <summary>
    /// 图片像素宽高，复制入库时一并记下。
    /// 之前是列表绑定时才去读文件解码拿尺寸——几百张卡同时绑定就等于在 UI 线程上
    /// 串行解码几百个 PNG，正是展开剪贴板页掉帧的元凶之一。
    /// </summary>
    public int ImageW { get; set; }
    public int ImageH { get; set; }

    public List<string> Files { get; set; } = new();

    /// <summary>复制来源进程名，例如 chrome.exe</summary>
    public string Source { get; set; } = "";

    public DateTime Time { get; set; } = DateTime.Now;

    public bool Pinned { get => _pinned; set { Set(ref _pinned, value); OnPropertyChanged(nameof(PinGlyph)); } }

    [JsonIgnore] public string PinGlyph => Pinned ? "已固定" : "固定";

    [JsonIgnore] public bool IsImage => Kind == ClipKind.Image && ImageFile != null;

    [JsonIgnore] public string KindLabel => Kind switch
    {
        ClipKind.Image => "图片",
        ClipKind.File => "文件",
        ClipKind.Url => "URL",
        ClipKind.Code => "代码",
        _ => "文字"
    };

    /// <summary>卡片左上角那个小色块里的字</summary>
    [JsonIgnore] public string KindGlyph => Kind switch
    {
        ClipKind.Image => "图",
        ClipKind.File => "件",
        ClipKind.Url => "链",
        ClipKind.Code => "码",
        _ => "文"
    };

    // macOS 系统色：蓝 / 紫 / 绿 / 青 / 橙
    [JsonIgnore] public string KindColor => Kind switch
    {
        ClipKind.Image => "#FFBF5AF2",
        ClipKind.File => "#FF30D158",
        ClipKind.Url => "#FF64D2FF",
        ClipKind.Code => "#FFFF9F0A",
        _ => "#FF0A84FF"
    };

    /// <summary>列表里显示的正文：文件列表、或文本前若干行</summary>
    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (Kind == ClipKind.File && Files.Count > 0)
                return Files.Count == 1
                    ? Path.GetFileName(Files[0])
                    : $"{Path.GetFileName(Files[0])}  等 {Files.Count} 个文件";

            var t = (Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (t.Length == 0) return "(空)";
            return t.Length > 600 ? t[..600] + "…" : t;
        }
    }

    [JsonIgnore] public bool HasImage => IsImage;

    [JsonIgnore] public bool ShowText => !IsImage;

    /// <summary>图片卡片上显示的尺寸文字（用入库时记录的宽高，不解码文件）</summary>
    [JsonIgnore]
    public string ImageSizeText => IsImage && ImageW > 0 ? $"{ImageW} × {ImageH}" : "图片";

    [JsonIgnore]
    public string TimeText
    {
        get
        {
            var d = DateTime.Now - Time;
            if (d.TotalSeconds < 60) return "刚刚";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} 分钟前";
            if (Time.Date == DateTime.Today) return Time.ToString("HH:mm");
            if (Time.Date == DateTime.Today.AddDays(-1)) return "昨天 " + Time.ToString("HH:mm");
            return Time.ToString("MM-dd HH:mm");
        }
    }

    public void Touch() => OnPropertyChanged(nameof(TimeText));
}

/// <summary>
/// 剪贴板历史的存储：JSON 索引 + 图片单独落盘，最多 50 条。
/// 超出时优先淘汰最旧的「未固定」条目（固定项不占名额，也不会被挤掉）。
///
/// 持久化保证：每次增删改都立即落盘，写法是「临时文件 + File.Replace 原子替换」，
/// 旧索引自动转成 .bak；万一主索引损坏，启动时回退读 .bak。重启不会丢记录。
/// </summary>
public static class ClipStore
{
    public const int MaxItems = 50;

    public static readonly ObservableCollection<ClipItem> Items = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string IndexPath => Path.Combine(Store.Dir, "clips.json");
    private static string ImageDir => Path.Combine(Store.Dir, "clips");

    public static string ImagePathOf(string fileName) => Path.Combine(ImageDir, fileName);

    public static void Load()
    {
        Items.Clear();

        var list = ReadIndex(IndexPath);
        if (list == null && File.Exists(IndexPath + ".bak"))
        {
            list = ReadIndex(IndexPath + ".bak");
            if (list != null) Log.Write("clip: 主索引不可读，已从 .bak 恢复");
        }

        if (list == null)
        {
            if (File.Exists(IndexPath)) Log.Write("clip: 索引不可读，本次历史为空");
            return;
        }

        foreach (var it in list.OrderByDescending(x => x.Time)) Items.Add(it);
        Log.Write($"clip: 载入 {Items.Count} 条历史");
    }

    private static List<ClipItem>? ReadIndex(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            Log.Ex($"读取剪贴板索引失败（{Path.GetFileName(path)}）", ex);
            return null;
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var json = JsonSerializer.Serialize(Items.ToList(), Json);

            // 先写临时文件，再原子替换：写一半进程被杀也不会把历史弄坏
            var tmp = IndexPath + ".tmp";
            var bak = IndexPath + ".bak";
            File.WriteAllText(tmp, json);
            if (File.Exists(IndexPath)) File.Replace(tmp, IndexPath, bak);
            else File.Move(tmp, IndexPath);
        }
        catch (Exception ex) { Log.Ex("剪贴板历史保存失败", ex); }
    }

    public static void Add(ClipItem item)
    {
        Items.Insert(0, item);
        Trim();
        Log.Write($"clip: 新增 {item.Kind} 后共 {Items.Count} 条");
        Save();
    }

    public static void Remove(ClipItem item)
    {
        Items.Remove(item);
        DeleteImage(item);
        Save();
    }

    public static void Clear(bool keepPinned)
    {
        if (keepPinned)
        {
            foreach (var it in Items.Where(x => !x.Pinned).ToList()) DeleteImage(it);
            for (int i = Items.Count - 1; i >= 0; i--)
                if (!Items[i].Pinned) Items.RemoveAt(i);
        }
        else
        {
            foreach (var it in Items.ToList()) DeleteImage(it);
            Items.Clear();
        }
        Save();
    }

    public static void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
    }

    private static void Trim()
    {
        // 列表是「最新在前」，所以从尾部往前找最旧的未固定项
        while (Items.Count > MaxItems)
        {
            var idx = -1;
            for (int i = Items.Count - 1; i >= 0; i--)
                if (!Items[i].Pinned) { idx = i; break; }

            if (idx < 0) break;          // 全都固定了，就允许超
            var victim = Items[idx];
            Items.RemoveAt(idx);
            DeleteImage(victim);
        }
    }

    public static string SaveImage(byte[] pngBytes, out string fileName)
    {
        Directory.CreateDirectory(ImageDir);
        fileName = DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6] + ".png";
        var path = Path.Combine(ImageDir, fileName);
        File.WriteAllBytes(path, pngBytes);
        return path;
    }

    private static void DeleteImage(ClipItem item)
    {
        if (item.ImageFile == null) return;
        try
        {
            var path = ImagePathOf(item.ImageFile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
