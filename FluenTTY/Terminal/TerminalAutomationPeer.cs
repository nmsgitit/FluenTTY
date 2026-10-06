using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace FluenTTY.Terminal;

/// <summary>
/// UI Automation for the terminal: the Value pattern returns the visible screen text (screen readers,
/// accessibility tools) and setting it types the text into the session as if it came from the keyboard.
/// tools\dev\ui.ps1 uses this to drive terminals in tests without sending real keystrokes.
/// </summary>
public sealed class TerminalAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
{
    private readonly TerminalCanvas _canvas;

    public TerminalAutomationPeer(TerminalCanvas owner) : base(owner) => _canvas = owner;

    protected override string GetClassNameCore() => "TerminalCanvas";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => string.IsNullOrEmpty(base.GetNameCore()) ? "Terminal" : base.GetNameCore();
    protected override bool IsKeyboardFocusableCore() => true;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

    public bool IsReadOnly => !_canvas.IsInputEnabled;

    public string Value => _canvas.GetScreenText();

    public void SetValue(string value)
    {
        if (IsReadOnly) throw new ElementNotEnabledException();
        // Newlines become Enter, like typing.
        _canvas.TypeText(value.Replace("\r\n", "\r").Replace('\n', '\r'));
    }
}
