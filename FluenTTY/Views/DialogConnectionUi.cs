using System.Windows;
using System.Windows.Threading;
using FluenTTY.Connections;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>Connection prompts as dialogs, for tabs that have no terminal to prompt in (SFTP/FTP, VNC, RDP).</summary>
public sealed class DialogConnectionUi : IConnectionUi
{
    private readonly FrameworkElement _owner;
    private readonly string _context;
    private readonly List<string> _log = new();

    public DialogConnectionUi(FrameworkElement owner, string context)
    {
        _owner = owner;
        _context = context;
    }

    public IReadOnlyList<string> Log { get { lock (_log) return _log.ToList(); } }
    public event Action<string>? StatusChanged;

    private Dispatcher D => _owner.Dispatcher;

    public string? Prompt(string prompt, bool echo) => D.Invoke(() =>
    {
        var label = prompt.Trim().TrimEnd(':');
        return FluentDialog.PromptText(Window.GetWindow(_owner), _context, label + ":", "", password: !echo);
    });

    public bool? ConfirmHostKey(HostKeyInfo info) => D.Invoke(() => HostKeyDialog.Show(Window.GetWindow(_owner), info));

    public bool Confirm(string title, string message, string yes, string no) =>
        D.Invoke(() => FluentDialog.Confirm(Window.GetWindow(_owner), title, message, yes, no));

    public void Status(string message)
    {
        lock (_log) _log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{message}");
        Diagnostics.Log($"[{_context}] {message}");
        D.BeginInvoke(() => StatusChanged?.Invoke(message));
    }

    public void Display(string text) => Status(text.Trim());
}
