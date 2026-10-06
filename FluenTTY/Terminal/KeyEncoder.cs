using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using FluenTTY.Models;

namespace FluenTTY.Terminal;

/// <summary>
/// Turns key presses that don't produce ordinary text into the byte sequences a VT/xterm host
/// expects. Follows PuTTY's terminal.c format_arrow_key / format_function_key /
/// format_small_keypad_key / format_numeric_keypad_key, including its keyboard options.
/// Plain printable characters are left to WPF's TextInput (so layouts, dead keys and AltGr work).
/// </summary>
public static class KeyEncoder
{
    public sealed class State
    {
        public bool AppCursor;
        public bool AppKeypad;
        public bool? BackarrowSendsBs;
        public bool NewLineMode;
    }

    /// <summary>Returns the sequence to send, or null to let the key through to text input.</summary>
    public static string? Encode(Key key, ModifierKeys mods, KeyboardSettings kb, State st)
    {
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        int bitmap = (shift || ctrl || alt) ? 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0) : 0;
        string esc = alt && kb.AltSendsEscape ? "\x1b" : "";

        switch (key)
        {
            case Key.Up: return Arrow('A', ctrl, bitmap, kb, st);
            case Key.Down: return Arrow('B', ctrl, bitmap, kb, st);
            case Key.Right: return Arrow('C', ctrl, bitmap, kb, st);
            case Key.Left: return Arrow('D', ctrl, bitmap, kb, st);

            case Key.Home: return SmallKeypad(1, bitmap, kb, st);
            case Key.Insert: return SmallKeypad(2, bitmap, kb, st);
            case Key.Delete: return SmallKeypad(3, bitmap, kb, st);
            case Key.End: return SmallKeypad(4, bitmap, kb, st);
            case Key.PageUp: return SmallKeypad(5, bitmap, kb, st);
            case Key.PageDown: return SmallKeypad(6, bitmap, kb, st);

            case >= Key.F1 and <= Key.F20:
                return FunctionKey(key - Key.F1 + 1, shift, ctrl, alt, bitmap, kb);

            case Key.Enter:
                return esc + (st.NewLineMode ? "\r\n" : "\r");
            case Key.Tab:
                if (ctrl) return null; // Ctrl+Tab belongs to the tab strip
                return shift ? "\x1b[Z" : esc + "\t";
            case Key.Escape:
                return esc + "\x1b";
            case Key.Back:
            {
                bool bs = kb.Backspace == BackspaceKey.ControlH;
                if (st.BackarrowSendsBs.HasValue) bs = st.BackarrowSendsBs.Value;
                if (ctrl) bs = !bs; // like PuTTY: Ctrl flips between ^H and ^?
                return esc + (bs ? "\b" : "\x7f");
            }
            case Key.Space:
                if (ctrl && !alt) return "\0";
                if (ctrl && alt) return "\x1b\0";
                if (alt && kb.AltSendsEscape) return "\x1b ";
                return null;

            case >= Key.NumPad0 and <= Key.NumPad9:
            case Key.Decimal: case Key.Add: case Key.Subtract: case Key.Multiply: case Key.Divide:
                return NumericKeypad(key, shift, ctrl, kb, st);
        }

        // Ctrl+key → control characters (unless Ctrl+Alt is AltGr and produces text).
        if (ctrl)
        {
            if (alt && kb.CtrlAltIsAltGr && ProducesText(key, shift, ctrlAlt: true)) return null;
            var c = ControlChar(key, shift);
            if (c != null) return (alt && kb.AltSendsEscape ? "\x1b" : "") + c;
            return null;
        }

        // Alt+key → ESC prefix ("meta sends escape").
        if (alt && kb.AltSendsEscape)
        {
            var text = KeyText(key, shift);
            if (!string.IsNullOrEmpty(text)) return "\x1b" + text;
        }
        return null;
    }

    private static string Arrow(char xkey, bool ctrl, int bitmap, KeyboardSettings kb, State st)
    {
        bool app = st.AppCursor;
        if (kb.ShiftedArrows == ShiftedArrowKeys.CtrlTogglesApplicationMode)
        {
            if (ctrl) app = !app;
            bitmap = 0;
        }
        if (app && bitmap == 0) return $"\x1bO{xkey}";
        if (bitmap != 0) return $"\x1b[1;{bitmap}{xkey}";
        return app ? $"\x1bO{xkey}" : $"\x1b[{xkey}";
    }

    private static string SmallKeypad(int code, int bitmap, KeyboardSettings kb, State st)
    {
        if (kb.FunctionKeys == FunctionKeys.Vt400 && code <= 6) code = "\0\u0002\u0001\u0004\u0005\u0003\u0006"[code];
        if (kb.FunctionKeys == FunctionKeys.Sco)
        {
            const string codes = "HL.FIG";
            return code == 3 ? "\x7f" : $"\x1b[{codes[code - 1]}";
        }
        if (code == 1 || code == 4)
        {
            switch (kb.HomeEnd)
            {
                case HomeEndKeys.Rxvt:
                    return code == 1 ? "\x1b[H" : "\x1bOw";
                case HomeEndKeys.Xterm:
                    char k = code == 1 ? 'H' : 'F';
                    if (bitmap != 0) return $"\x1b[1;{bitmap}{k}";
                    return st.AppCursor ? $"\x1bO{k}" : $"\x1b[{k}";
            }
        }
        if (bitmap != 0 && kb.FunctionKeys == FunctionKeys.Xterm216) return $"\x1b[{code};{bitmap}~";
        return $"\x1b[{code}~";
    }

    private static readonly int[] TildeCodes = { -1, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 31, 32, 33, 34 };

    private static string FunctionKey(int number, bool shift, bool ctrl, bool alt, int bitmap, KeyboardSettings kb)
    {
        int index = number;
        if (kb.FunctionKeys is not FunctionKeys.Xterm216 and not FunctionKeys.Sco && shift && index <= 10)
        {
            shift = false;
            index += 10;
        }
        if (index >= TildeCodes.Length) index = TildeCodes.Length - 1;
        int code = TildeCodes[index];

        switch (kb.FunctionKeys)
        {
            case FunctionKeys.Sco:
            {
                const string sco = "MNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz@[\\]^_`{";
                int i = number >= 1 && number <= 12 ? number - 1 : 0;
                if (shift) i += 12;
                if (ctrl) i += 24;
                return $"\x1b[{sco[i]}";
            }
            case FunctionKeys.Vt100Plus when code >= 11 && code <= 24:
            {
                int offt = code > 15 ? 1 : 0;
                if (code > 21) offt++;
                return $"\x1bO{(char)(code + 'P' - 11 - offt)}";
            }
            case FunctionKeys.Linux when code >= 11 && code <= 15:
                return $"\x1b[[{(char)(code + 'A' - 11)}";
            case FunctionKeys.XtermR6 or FunctionKeys.Xterm216 when code >= 11 && code <= 14:
                if (kb.FunctionKeys == FunctionKeys.Xterm216 && bitmap != 0)
                    return $"\x1b[1;{bitmap}{(char)(code + 'P' - 11)}";
                return $"\x1bO{(char)(code + 'P' - 11)}";
        }
        if (kb.FunctionKeys == FunctionKeys.Xterm216 && bitmap != 0) return $"\x1b[{code};{bitmap}~";
        return $"\x1b[{code}~";
    }

    private static string? NumericKeypad(Key key, bool shift, bool ctrl, KeyboardSettings kb, State st)
    {
        char k = key switch
        {
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (key - Key.NumPad0)),
            Key.Decimal => '.',
            Key.Add => '+',
            Key.Subtract => '-',
            Key.Multiply => '*',
            Key.Divide => '/',
            _ => '\0',
        };
        if (kb.Keypad == InitialKeypad.NetHack && k >= '1' && k <= '9')
        {
            const string nh = "bjnh.lyku";
            char c = nh[k - '1'];
            if (ctrl && c != '.') c = (char)(c & 0x1F);
            else if (shift && c != '.') c = char.ToUpperInvariant(c);
            return c.ToString();
        }
        if (!st.AppKeypad) return null; // normal mode: the digit arrives as text
        char x = k switch
        {
            '/' => 'o', '*' => 'j', '-' => 'm', '+' => 'l', '.' => 'n',
            _ => (char)('p' + (k - '0')),
        };
        if (kb.FunctionKeys == FunctionKeys.XtermR6 && k == '+') x = shift ? 'l' : 'k';
        return $"\x1bO{x}";
    }

    private static string? ControlChar(Key key, bool shift)
    {
        if (key >= Key.A && key <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
        return key switch
        {
            Key.D2 => "\0",
            Key.D3 or Key.Oem4 => "\x1b",     // Ctrl+[ and Ctrl+3
            Key.D4 or Key.Oem5 => "\x1c",     // Ctrl+\ and Ctrl+4
            Key.D5 or Key.Oem6 => "\x1d",     // Ctrl+] and Ctrl+5
            Key.D6 => "\x1e",                  // Ctrl+^ (Ctrl+6)
            Key.D7 or Key.OemMinus or Key.Oem2 => "\x1f", // Ctrl+_ and Ctrl+/
            Key.D8 => "\x7f",
            _ => null,
        };
    }

    // ------------------------------------------------------------------ keyboard layout helpers

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint virtKey, uint scanCode, byte[] keyState, [Out] StringBuilder buff, int buffSize, uint flags, IntPtr hkl);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint thread);

    /// <summary>The text the key would type with the given modifiers in the current keyboard layout.</summary>
    public static string? KeyText(Key key, bool shift, bool ctrlAlt = false)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return null;
        var state = new byte[256];
        if (shift) state[0x10] = 0x80;
        if (ctrlAlt) { state[0x11] = 0x80; state[0x12] = 0x80; }
        var hkl = GetKeyboardLayout(0);
        uint scan = MapVirtualKeyEx((uint)vk, 0, hkl);
        var sb = new StringBuilder(8);
        // flag 4: don't change the keyboard state (keeps dead keys pending for the real input).
        int n = ToUnicodeEx((uint)vk, scan, state, sb, sb.Capacity, 4, hkl);
        if (n <= 0) return null;
        var s = sb.ToString(0, n);
        return s.Length > 0 && s[0] >= ' ' ? s : null;
    }

    private static bool ProducesText(Key key, bool shift, bool ctrlAlt) => KeyText(key, shift, ctrlAlt) != null;
}
