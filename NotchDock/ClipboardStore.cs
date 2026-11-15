using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchDock;

public enum ClipKind { Text, Image, File, Url, Code }

/// <summary>一条剪贴板历史。</summary>
public sealed class ClipItem : ObservableObject
{
    private bool _pinned;
    private string? _cachedImagePath;
    private BitmapSource? _image;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; } = ClipKind.Text;
    public string Text { get; set; } = "";

    /// <summary>图片文件名（相对 clips 目录），文本类为 null</summary>
    public string? ImageFile { get; set; }

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

    /// <summary>图片卡片上显示的尺寸文字</summary>
    [JsonIgnore]
    public string ImageSizeText
    {
        get
        {
            var src = Image;
            return src == null ? "图片" : $"{src.PixelWidth} × {src.PixelHeight}";
        }
    }

    /// <summary>用 ImageBrush 而不是 Image 控件——分层窗口里 Image 控件偶发不绘制</summary>
    [JsonIgnore]
    public ImageBrush? ImageBrush
    {
        get
        {
            var src = Image;
            if (src == null) return null;
            var brush = new ImageBrush(src)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            brush.Freeze();
            return brush;
        }
    }

    [JsonIgnore]
    public BitmapSource? Image
    {
        get
        {
            if (!IsImage) return null;
            if (_image != null && _cachedImagePath == ImageFile) return _image;

            try
            {
                var path = ClipStore.ImagePathOf(ImageFile!);
                if (!File.Exists(path))
                {
                    Log.Write($"clip: 图片文件不存在 {path}");
                    return null;
                }

                // 用 BitmapFrame 直接拿到"已完全解码"的一帧。
                // BitmapImage + StreamSource 这条路过 Freeze 之后在分层窗口里画不出来。
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

                // 再包一层 CachedBitmap：分层窗口只会真正绘制"已缓存"的位图，
                // 直接拿 BitmapFrame 会得到一块空白的矩形。
                var cached = new CachedBitmap(frame, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                cached.Freeze();

                _image = cached;
                _cachedImagePath = ImageFile;
                Log.Write($"clip: 图片加载成功 {cached.PixelWidth}x{cached.PixelHeight} {ImageFile}");
                return _image;
            }
            catch (Exception ex)
            {
                Log.Write($"clip: 图片加载失败 {ImageFile} -> {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }
    }

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
/// 剪贴板历史的存储：JSON 索引 + 图片单独落盘，最多 500 条。
/// 超出时优先淘汰最旧的「未固定」条目。
/// </summary>
public static class ClipStore
{
    public const int MaxItems = 500;

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
        try
        {
            Items.Clear();
            if (!File.Exists(IndexPath)) return;

            var json = File.ReadAllText(IndexPath);
            var list = JsonSerializer.Deserialize<List<ClipItem>>(json, Json);
            if (list == null) return;

            foreach (var it in list.OrderByDescending(x => x.Time)) Items.Add(it);
            Log.Write($"clip: 载入 {Items.Count} 条历史");
        }
        catch (Exception ex) { Log.Ex("剪贴板历史载入失败", ex); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var json = JsonSerializer.Serialize(Items.ToList(), Json);

            // 先写临时文件再替换，避免写一半崩溃把历史弄坏
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, IndexPath, true);
            File.Delete(tmp);
            Log.Write($"clip: 写入 {Items.Count} 条");
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
