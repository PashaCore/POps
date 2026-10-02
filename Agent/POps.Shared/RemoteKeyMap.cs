using System;
using System.Collections.Generic;
using System.Linq;

namespace POps.Shared
{
    // Panelin uzaktan klavye olayı: {"input_type":"keyboard","key":e.key,"code":e.code,"is_down":..,
    //  "ctrl","alt","shift","meta","altgr"}. Tarayıcının key/code değerleri tepsinin SendInput'una çevrilir.
    // Eskiden tek karakter büyük harfe çevrilip sanal tuş sanılıyordu: İ (U+0130) 0x30 ("0") oluyor, ş/ğ/@/€ yanlış
    // ya da hiç yazılmıyordu. Kurallar:
    //  1. Adlı tuşlar (Enter, F5, Home, ...): sanal tuş; sol/sağ Shift/Ctrl/Alt/Win code'dan; ok, Home/End/Insert/
    //     Delete/PageUp/PageDown, sağ Ctrl/Alt, Win, NumLock, PrintScreen, Menü, sayısal Enter genişletilmiş tuştur.
    //  2. Tek yazdırılabilir karakter (bir Unicode kod noktası):
    //     a. Kısayol (ctrl XOR alt, ya da meta) ve AltGr değil: sanal tuş code'dan (KeyA-Z, Digit0-9, Space), yoksa
    //        ön plandaki pencerenin klavye düzeninde VkKeyScanEx. Ctrl+C, Win+R çalışır.
    //     b. Diğer her durumda: KEYEVENTF_UNICODE ile UTF-16 birimleri (vekil çift iki olay). Düzenden bağımsızdır.
    //  3. code yoksa (eski panel): ASCII harf, rakam ve boşluk eski yoldan (sanal tuş), diğer tek karakterler Unicode.
    public struct RemoteKeyModifiers
    {
        public bool Ctrl, Alt, Shift, Meta, AltGr;
    }

    public sealed class RemoteKeyStroke
    {
        private RemoteKeyStroke(ushort virtualKey, bool extended, char[] units)
        {
            VirtualKey = virtualKey;
            Extended = extended;
            Units = units ?? Array.Empty<char>();
        }

        // 0: Unicode olay (Units)
        public ushort VirtualKey { get; }
        public bool Extended { get; }
        public IReadOnlyList<char> Units { get; }
        public bool IsUnicode => VirtualKey == 0;

        public static RemoteKeyStroke Key(ushort vk, bool extended = false) => new RemoteKeyStroke(vk, extended, null);
        public static RemoteKeyStroke Unicode(string text) => new RemoteKeyStroke(0, false, text.ToCharArray());

        public override string ToString() => IsUnicode ? "U+" + string.Join("+", Units.Select(u => ((int)u).ToString("X4"))) : $"VK 0x{VirtualKey:X2}{(Extended ? " ext" : "")}";
    }

    public static class RemoteKeyMap
    {
        public const ushort VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
        public const ushort VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;
        public const ushort VkLWin = 0x5B, VkRWin = 0x5C;

        private static readonly Dictionary<string, (ushort Vk, bool Ext)> Named = BuildNamed();

        private static Dictionary<string, (ushort, bool)> BuildNamed()
        {
            var map = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal)
            {
                ["Enter"] = (0x0D, false),
                ["Backspace"] = (0x08, false),
                ["Tab"] = (0x09, false),
                ["Escape"] = (0x1B, false),
                ["Esc"] = (0x1B, false),
                ["Space"] = (0x20, false),
                ["Spacebar"] = (0x20, false),
                ["ArrowLeft"] = (0x25, true),
                ["ArrowUp"] = (0x26, true),
                ["ArrowRight"] = (0x27, true),
                ["ArrowDown"] = (0x28, true),
                ["PageUp"] = (0x21, true),
                ["PageDown"] = (0x22, true),
                ["End"] = (0x23, true),
                ["Home"] = (0x24, true),
                ["Insert"] = (0x2D, true),
                ["Delete"] = (0x2E, true),
                ["CapsLock"] = (0x14, false),
                ["NumLock"] = (0x90, true),
                ["ScrollLock"] = (0x91, false),
                ["PrintScreen"] = (0x2C, true),
                ["Pause"] = (0x13, false),
                ["ContextMenu"] = (0x5D, true),
                ["AltGraph"] = (VkRMenu, true),
            };
            for (int i = 1; i <= 24; i++) map["F" + i] = ((ushort)(0x70 + i - 1), false);
            return map;
        }

        // vkKeyScan: ön plandaki pencerenin klavye düzeninde VkKeyScanEx (düşük bayt sanal tuş; -1 karşılığı yok).
        // Dönen null: gönderilecek bir şey yok (boş, "Dead", "Unidentified", denetim karakteri, bilinmeyen ad).
        public static RemoteKeyStroke Map(string key, string code, RemoteKeyModifiers mods, Func<char, short> vkKeyScan = null)
        {
            if (string.IsNullOrEmpty(key)) return null;

            if (!IsSingleCodePoint(key))
                return MapNamed(key, code);

            if (key.Length == 1 && char.IsControl(key[0])) return null;
            bool hasCode = !string.IsNullOrEmpty(code);

            if (!hasCode)
            {
                // Eski panel: ASCII harf/rakam/boşluk eskisi gibi sanal tuş (Ctrl+C gibi kısayollar da böyle çalışıyordu)
                ushort legacy = AsciiVirtualKey(key[0]);
                return legacy != 0 && key.Length == 1 ? RemoteKeyStroke.Key(legacy) : RemoteKeyStroke.Unicode(key);
            }

            bool shortcut = !mods.AltGr && ((mods.Ctrl ^ mods.Alt) || mods.Meta);
            if (shortcut)
            {
                ushort vk = CodeVirtualKey(code);
                if (vk == 0 && key.Length == 1 && vkKeyScan != null)
                {
                    short scanned = vkKeyScan(key[0]);
                    if (scanned != -1 && (scanned & 0xFF) != 0xFF) vk = (ushort)(scanned & 0xFF);
                }
                if (vk == 0 && key.Length == 1) vk = AsciiVirtualKey(key[0]);
                if (vk != 0) return RemoteKeyStroke.Key(vk);
            }
            return RemoteKeyStroke.Unicode(key);
        }

        private static RemoteKeyStroke MapNamed(string key, string code)
        {
            switch (key)
            {
                case "Shift":
                    return RemoteKeyStroke.Key(code == "ShiftRight" ? VkRShift : code == "ShiftLeft" ? VkLShift : VkShift);
                case "Control":
                    return code == "ControlRight" ? RemoteKeyStroke.Key(VkRControl, true) : RemoteKeyStroke.Key(code == "ControlLeft" ? VkLControl : VkControl);
                case "Alt":
                    return code == "AltRight" ? RemoteKeyStroke.Key(VkRMenu, true) : RemoteKeyStroke.Key(code == "AltLeft" ? VkLMenu : VkMenu);
                case "Meta":
                case "OS":
                    return RemoteKeyStroke.Key(code == "MetaRight" || code == "OSRight" ? VkRWin : VkLWin, true);
                case "Enter":
                    // Sayısal tuş takımının Enter'ı genişletilmiş tuştur
                    return RemoteKeyStroke.Key(0x0D, code == "NumpadEnter");
            }
            return Named.TryGetValue(key, out var mapped) ? RemoteKeyStroke.Key(mapped.Vk, mapped.Ext) : null;
        }

        private static bool IsSingleCodePoint(string s) =>
            s.Length == 1 || (s.Length == 2 && char.IsSurrogatePair(s[0], s[1]));

        // KeyA-KeyZ -> 0x41-0x5A, Digit0-Digit9 -> 0x30-0x39, Space -> 0x20; diğerleri 0
        public static ushort CodeVirtualKey(string code)
        {
            if (string.IsNullOrEmpty(code)) return 0;
            if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && code[3] >= 'A' && code[3] <= 'Z') return (ushort)code[3];
            if (code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && code[5] >= '0' && code[5] <= '9') return (ushort)code[5];
            return code == "Space" ? (ushort)0x20 : (ushort)0;
        }

        private static ushort AsciiVirtualKey(char c)
        {
            if (c >= 'a' && c <= 'z') return (ushort)(c - 'a' + 'A');
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
            return c == ' ' ? (ushort)0x20 : (ushort)0;
        }
    }

    // Tepsinin bastığı (bırakmadığı) sanal tuşlar: kontrol oturumu bitince ya da servis bağlantısı kopunca bırakılır,
    // böylece panelden basılmış bir Ctrl/Shift/Win makinede basılı kalmaz. Unicode olaylar takılı kalmaz.
    public sealed class PressedKeys
    {
        private readonly Dictionary<ushort, bool> _down = new Dictionary<ushort, bool>();
        private readonly object _gate = new object();

        public void Track(RemoteKeyStroke stroke, bool isDown)
        {
            if (stroke == null || stroke.IsUnicode) return;
            lock (_gate)
            {
                if (isDown) _down[stroke.VirtualKey] = stroke.Extended;
                else _down.Remove(stroke.VirtualKey);
            }
        }

        public int Count { get { lock (_gate) return _down.Count; } }

        // Bırakılacak tuşlar; liste boşaltılır
        public List<RemoteKeyStroke> TakeAll()
        {
            lock (_gate)
            {
                List<RemoteKeyStroke> keys = _down.Select(kv => RemoteKeyStroke.Key(kv.Key, kv.Value)).ToList();
                _down.Clear();
                return keys;
            }
        }
    }
}
