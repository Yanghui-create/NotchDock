using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotchDock;

public static class Store
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchDock");

    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppConfig Config { get; private set; } = new();

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
            Config.Favorites ??= new List<FavoriteItem>();
            Config.Todos ??= new List<TodoItem>();
            }
        }
        catch (Exception ex)
        {
            Log.Ex("读取配置失败", ex);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
            Config = new AppConfig();
        }
        // 清理悬空条目
        Config.Favorites.RemoveAll(f => !f.Exists);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, Options));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex) { Log.Ex("保存配置失败", ex); }
    }
}
