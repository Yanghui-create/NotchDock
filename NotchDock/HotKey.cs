namespace NotchDock;

/// <summary>修饰键。数值刻意与 Win32 的 MOD_* 保持一致，注册时可直接强转。</summary>
[Flags]
public enum HotKeyMods
{
    None = 0,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

/// <summary>
/// 一个组合键。字符串形式用于配置持久化与界面显示，例如 "Win+V"、"Ctrl+Alt+Shift+K"。
/// </summary>
public readonly struct HotKey : IEquatable<HotKey>
{
    public HotKeyMods Mods { get; }
    public uint Vk { get; }

    public HotKey(HotKeyMods mods, uint vk)
    {
        Mods = mods;
        Vk = vk;
    }

    public bool IsValid => Vk != 0 && Mods != HotKeyMods.None;

    public static HotKey Default => new(HotKeyMods.Win, 0x56);   // Win + V

    /// <summary>顺序固定为 Ctrl → Alt → Shift → Win，和界面显示一致</summary>
    public string Display
    {
        get
        {
            if (Vk == 0) return "未设置";

            var parts = new List<string>(4);
            if (Mods.HasFlag(HotKeyMods.Ctrl)) parts.Add("Ctrl");
            if (Mods.HasFlag(HotKeyMods.Alt)) parts.Add("Alt");
            if (Mods.HasFlag(HotKeyMods.Shift)) parts.Add("Shift");
            if (Mods.HasFlag(HotKeyMods.Win)) parts.Add("Win");
            parts.Add(VkName(Vk));
            return string.Join("+", parts);
        }
    }

    /// <summary>界面上按键帽那种显示：Ctrl + Alt + V</summary>
    public string Pretty => Vk == 0 ? "未设置" : Display.Replace("+", " + ");

    public override string ToString() => Display;

    public bool Equals(HotKey other) => Mods == other.Mods && Vk == other.Vk;
    public override bool Equals(object? obj) => obj is HotKey h && Equals(h);
    public override int GetHashCode() => HashCode.Combine(Mods, Vk);

    // ===================== 解析 / 命名 =====================

    public static bool TryParse(string? text, out HotKey hk)
    {
        hk = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        HotKeyMods mods = HotKeyMods.None;
        uint vk = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;

            switch (t.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotKeyMods.Ctrl; continue;
                case "alt" or "menu": mods |= HotKeyMods.Alt; continue;
                case "shift": mods |= HotKeyMods.Shift; continue;
                case "win" or "windows" or "meta" or "super": mods |= HotKeyMods.Win; continue;
            }

            if (vk != 0) return false;                  // 只能有一个主键
            if (!TryParseKey(t, out vk)) return false;
        }

        if (vk == 0 || mods == HotKeyMods.None) return false;   // 必须有修饰键，避免吞掉普通打字
        hk = new HotKey(mods, vk);
        return true;
    }

    private static bool TryParseKey(string t, out uint vk)
    {
        vk = 0;
        if (t.Length == 1)
        {
            var c = char.ToUpperInvariant(t[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = c; return true; }

            vk = t[0] switch
            {
                '`' => 0xC0,
                '-' => 0xBD,
                '=' => 0xBB,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                ';' => 0xBA,
                '\'' => 0xDE,
                ',' => 0xBC,
                '.' => 0xBE,
                '/' => 0xBF,
                _ => 0
            };
            return vk != 0;
        }

        var lower = t.ToLowerInvariant();

        if (lower.StartsWith('f') && int.TryParse(lower[1..], out var fn) && fn >= 1 && fn <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = lower switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "backspace" => 0x08,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "left" => 0x25,
            "right" => 0x27,
            "up" => 0x26,
            "down" => 0x28,
            "printscreen" or "prtsc" => 0x2C,
            "pause" => 0x13,
            _ => 0
        };
        return vk != 0;
    }

    /// <summary>虚拟键码 → 可读名字（只覆盖常见键，够录制用）</summary>
    public static string VkName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);

        return vk switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x27 => "Right",
            0x26 => "Up",
            0x28 => "Down",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            _ => "VK" + vk.ToString("X2")
        };
    }
}
