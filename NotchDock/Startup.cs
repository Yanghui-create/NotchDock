using Microsoft.Win32;

namespace NotchDock;

/// <summary>开机自启（写入 HKCU Run，免安装、免管理员）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NotchDock";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var v = key?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v) && v.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Ex("设置自启失败", ex); }
    }
}
