using System.Diagnostics;
using Microsoft.Win32;

namespace NotchDock;

/// <summary>
/// 「接管 Win+V」的系统级优化。机制与 TieZ 完全相同，源自其开源实现
/// （github.com/jimuzhe/tiez-clipboard，GPL-3.0，system_cmd.rs 的
/// trigger_registry_win_v_optimization / set_windows_clipboard_history）：
///
/// Windows 的 Win+V 由 explorer（剪贴板历史）注册，RegisterHotKey 先到先得，
/// 系统开着剪贴板历史我们就永远抢不到——TieZ 的注释写得很直白：
/// "Windows disables Explorer's Win+V handler before assigning it to TieZ"。
/// 它动用的是 Explorer 的官方注册表开关 DisabledHotkeys：把 V 加进去后，
/// explorer 在系统层面不再处理 Win+V，这组键就被让出来了：
///
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\DisabledHotkeys += "V"
///   HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory  = 0
///   HKCU\Software\Microsoft\Clipboard\EnableCloudClipboard    = 0
///
/// 再重启一次 explorer：旧进程一死，它注册的 Win+V 立即释放；新进程读到
/// 禁用列表，不会再抢。副作用是系统自带的 Win+V 历史面板不再出现——
/// NotchDock 自己就有剪贴板历史，正好顶上。可用 Revert() 随时撤销。
/// </summary>
internal static class WinVOptimizer
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClipboardKey = @"Software\Microsoft\Clipboard";

    /// <summary>DisabledHotkeys 里是否已包含 V（即已做过系统级接管）</summary>
    public static bool IsOptimized()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return (key?.GetValue("DisabledHotkeys") as string ?? "")
                .ToUpperInvariant().Contains('V');
        }
        catch { return false; }
    }

    /// <summary>禁用系统对 Win+V 的处理（TieZ 同款）。返回是否有实际改动。</summary>
    public static bool Apply()
    {
        try
        {
            var changed = false;

            using (var adv = Registry.CurrentUser.CreateSubKey(AdvancedKey))
            {
                var current = adv.GetValue("DisabledHotkeys") as string ?? "";
                if (!current.ToUpperInvariant().Contains('V'))
                {
                    adv.SetValue("DisabledHotkeys", current + "V");
                    changed = true;
                }
            }

            using (var cb = Registry.CurrentUser.CreateSubKey(ClipboardKey))
            {
                foreach (var name in new[] { "EnableClipboardHistory", "EnableCloudClipboard" })
                {
                    if (cb.GetValue(name) is int v && v == 0) continue;
                    cb.SetValue(name, 0);
                    changed = true;
                }
            }

            if (changed) Log.Write("winv: 已写入 DisabledHotkeys=V 并关闭系统剪贴板历史");
            return changed;
        }
        catch (Exception ex) { Log.Ex("禁用系统 Win+V 失败", ex); return false; }
    }

    /// <summary>撤销优化，把 Win+V 还给系统剪贴板历史。返回是否有实际改动。</summary>
    public static bool Revert()
    {
        try
        {
            var changed = false;

            using (var adv = Registry.CurrentUser.CreateSubKey(AdvancedKey))
            {
                var current = adv.GetValue("DisabledHotkeys") as string ?? "";
                var upper = current.ToUpperInvariant();
                if (upper.Contains('V'))
                {
                    var clean = upper.Replace("V", "");
                    if (clean.Length == 0) adv.DeleteValue("DisabledHotkeys", false);
                    else adv.SetValue("DisabledHotkeys", clean);
                    changed = true;
                }
            }

            using (var cb = Registry.CurrentUser.CreateSubKey(ClipboardKey))
            {
                foreach (var name in new[] { "EnableClipboardHistory", "EnableCloudClipboard" })
                {
                    if ((cb.GetValue(name) as int? ?? 1) != 1)
                    {
                        cb.SetValue(name, 1);
                        changed = true;
                    }
                }
            }

            if (changed) Log.Write("winv: 已撤销 DisabledHotkeys 优化，恢复系统剪贴板历史");
            return changed;
        }
        catch (Exception ex) { Log.Ex("恢复系统 Win+V 失败", ex); return false; }
    }

    /// <summary>
    /// 重启 explorer（TieZ 同款命令）：旧进程一死它注册的 Win+V 立即释放，
    /// 新进程读到 DisabledHotkeys 不会再抢。桌面/任务栏会闪几秒，属正常。
    /// </summary>
    public static void RestartExplorer()
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/C taskkill /F /IM explorer.exe & start explorer.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
            Log.Write("winv: 已重启 explorer 以释放 Win+V");
        }
        catch (Exception ex) { Log.Ex("重启 explorer 失败", ex); }
    }
}
