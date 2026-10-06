using System.Text;

namespace TestServers;

/// <summary>
/// A tiny line-oriented "shell" for exercising the terminal: echo, prompts, colours, Unicode, the
/// alternate screen, mouse reporting, titles (OSC 2) and working-directory reports (OSC 7).
/// </summary>
public sealed class FakeShell
{
    private readonly Stream _io;
    private readonly string _root;
    private string _cwd = "/";
    private readonly StringBuilder _line = new();
    private readonly string _user;
    public int Cols = 80, Rows = 24;
    private bool _inAlt, _inMouse;

    public FakeShell(Stream io, string root, string user)
    {
        _io = io;
        _root = root;
        _user = user;
    }

    private void W(string s)
    {
        var b = Encoding.UTF8.GetBytes(s.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        _io.Write(b, 0, b.Length);
        _io.Flush();
    }

    private void Prompt()
    {
        W($"\x1b]7;file://sandbox{_cwd}\x07");
        W($"\x1b[1;32m{_user}@sandbox\x1b[0m:\x1b[1;34m{_cwd}\x1b[0m$ ");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        W($"Welcome to the FluenTTY test shell ({Cols}x{Rows}). Type 'help'.\n");
        Prompt();
        var buf = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await _io.ReadAsync(buf, ct); } catch { break; }
            if (n <= 0) break;
            int c = decoder.GetChars(buf, 0, n, chars, 0);
            for (int i = 0; i < c; i++)
            {
                if (!Handle(chars[i])) return;
            }
        }
    }

    private char _prev;

    private bool Handle(char ch)
    {
        // CR LF (Telnet New Line) is one Enter, not two.
        bool lfAfterCr = ch == '\n' && _prev == '\r';
        _prev = ch;
        if (lfAfterCr) return true;
        if (_inAlt || _inMouse)
        {
            if (ch == 'q')
            {
                if (_inMouse) W("\x1b[?1000l\x1b[?1006l\r\nmouse reporting off\n");
                if (_inAlt) W("\x1b[?1049l");
                _inAlt = _inMouse = false;
                Prompt();
            }
            else if (_inMouse && ch == '\x1b') W("\r\n<ESC>");
            else if (_inMouse) W(ch < ' ' ? $"^{(char)(ch + '@')}" : ch.ToString());
            return true;
        }
        switch (ch)
        {
            case '\r':
            case '\n':
                W("\n");
                var cmd = _line.ToString().Trim();
                _line.Clear();
                if (!Run(cmd)) return false;
                if (!_inAlt && !_inMouse) Prompt();
                return true;
            case '\x7f':
            case '\b':
                if (_line.Length > 0) { _line.Length--; W("\b \b"); }
                return true;
            case '\x03':
                _line.Clear();
                W("^C\n");
                Prompt();
                return true;
            case '\x04':
                if (_line.Length == 0) { W("logout\n"); return false; }
                return true;
            case '\x1b':
                return true; // ignore escape sequences (arrow keys) in this simple shell
            default:
                if (ch < ' ') return true;
                _line.Append(ch);
                W(ch.ToString());
                return true;
        }
    }

    private string Local(string path)
    {
        var p = path.StartsWith('/') ? path : (_cwd.TrimEnd('/') + "/" + path);
        var full = Path.GetFullPath(Path.Combine(_root, p.TrimStart('/')));
        return full.StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase) ? full : _root;
    }

    private bool Run(string cmd)
    {
        var parts = cmd.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "help":
                W("commands: ls, cd, pwd, cat FILE, echo, colors, unicode, bell, title TEXT, clear, alt, mouse, seq N, size, wide, exit\n");
                break;
            case "ls":
                foreach (var d in Directory.GetDirectories(Local(arg.Length > 0 ? arg : "."))) W($"\x1b[1;34m{Path.GetFileName(d)}/\x1b[0m\n");
                foreach (var f in Directory.GetFiles(Local(arg.Length > 0 ? arg : "."))) W($"{Path.GetFileName(f)}  \x1b[2m{new FileInfo(f).Length} bytes\x1b[0m\n");
                break;
            case "pwd": W(_cwd + "\n"); break;
            case "cd":
            {
                var target = arg.Length == 0 ? "/" : arg;
                var full = Local(target);
                if (!Directory.Exists(full)) { W($"cd: {target}: No such directory\n"); break; }
                var rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
                _cwd = rel == "." ? "/" : "/" + rel;
                break;
            }
            case "cat":
            {
                var f = Local(arg);
                if (File.Exists(f)) W(File.ReadAllText(f) + "\n"); else W($"cat: {arg}: No such file\n");
                break;
            }
            case "echo": W(arg + "\n"); break;
            case "colors":
                for (int i = 0; i < 16; i++) W($"\x1b[48;5;{i}m  {i,2}  ");
                W("\x1b[0m\n");
                for (int i = 16; i < 232; i++) { W($"\x1b[48;5;{i}m "); if ((i - 15) % 36 == 0) W("\x1b[0m\n"); }
                for (int i = 232; i < 256; i++) W($"\x1b[48;5;{i}m ");
                W("\x1b[0m\n");
                for (int i = 0; i < 64; i++) W($"\x1b[48;2;{i * 4};{255 - i * 4};128m ");
                W("\x1b[0m\n\x1b[1mbold\x1b[0m \x1b[2mfaint\x1b[0m \x1b[3mitalic\x1b[0m \x1b[4munderline\x1b[0m \x1b[7minverse\x1b[0m \x1b[9mstrike\x1b[0m \x1b[31mred\x1b[0m \x1b[1;31mbold red\x1b[0m \x1b[38;2;255;128;0mtruecolor orange\x1b[0m\n");
                break;
            case "unicode":
                W("┌────────┬────────┐\n│ box    │ drawing│\n└────────┴────────┘\n");
                W("CJK: 日本語テキスト 中文 한국어 | Emoji: 😀🚀 | Accents: café naïve e\u0301\n");
                W("\x1b(0lqqqqqk\nx DEC x\nmqqqqqj\x1b(B (DEC special graphics)\n");
                break;
            case "bell": W("\x07bell sent\n"); break;
            case "title": W($"\x1b]2;{arg}\x07"); break;
            case "clear": W("\x1b[H\x1b[2J"); break;
            case "size": W($"{Cols}x{Rows}\n"); break;
            case "wide": W(new string('#', Cols) + "\n" + string.Concat(Enumerable.Range(0, Cols / 10 + 1).Select(i => $"{i * 10,-10}")) + "\n"); break;
            case "seq":
                int count = int.TryParse(arg, out var nn) ? nn : 100;
                var sb = new StringBuilder();
                for (int i = 1; i <= count; i++) sb.Append($"line {i}\n");
                W(sb.ToString());
                break;
            case "alt":
                _inAlt = true;
                W("\x1b[?1049h\x1b[H\x1b[2J");
                W($"\x1b[1;1H\x1b[44;97m{"  Alternate screen — press q to return".PadRight(Cols)}\x1b[0m");
                for (int r = 3; r < Math.Min(Rows, 12); r++) W($"\x1b[{r};{4 + r}H\x1b[3{r % 8}m█ row {r}\x1b[0m");
                W($"\x1b[{Rows};1H\x1b[7m status line at the bottom \x1b[0m");
                break;
            case "mouse":
                _inMouse = true;
                W("SGR mouse reporting on: click/scroll in the terminal, q to stop\n\x1b[?1000h\x1b[?1006h");
                break;
            case "exit":
            case "logout":
                W("logout\n");
                return false;
            default:
                W($"{parts[0]}: command not found\n");
                break;
        }
        return true;
    }
}
