using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FluenTTY.Models;

namespace FluenTTY.Views;

public enum TabState { Idle, Connecting, Connected, Disconnected }

/// <summary>Where a tab lives: the main window's tab strip or a pop-out window.</summary>
public interface ITabHost
{
    Window HostWindow { get; }
    void CloseTab(SessionTab tab);
    void ActivateTab(SessionTab tab);
    /// <summary>Asks the host to flash / mark the tab (bell, activity).</summary>
    void Attention(SessionTab tab);
}

/// <summary>
/// Base for everything that can sit in a tab: terminals, remote desktops, file browsers, editors and
/// the start page. Tabs can move between the main window and pop-out windows, so they must not assume
/// a particular parent.
/// </summary>
public abstract class SessionTab : UserControl, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _title = "";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(); } }
    }

    private string _glyph = "";
    public string Glyph
    {
        get => _glyph;
        protected set { _glyph = value; Raise(); }
    }

    private TabState _state = TabState.Idle;
    public TabState State
    {
        get => _state;
        protected set { if (_state != value) { _state = value; Raise(); Raise(nameof(StateText)); } }
    }

    public string StateText => State switch
    {
        TabState.Connecting => "Connecting",
        TabState.Connected => "Connected",
        TabState.Disconnected => "Disconnected",
        _ => "",
    };

    private bool _hasAlert;
    /// <summary>Bell or activity while the tab wasn't selected.</summary>
    public bool HasAlert
    {
        get => _hasAlert;
        set { if (_hasAlert != value) { _hasAlert = value; Raise(); } }
    }

    private bool _isPoppedOut;
    public bool IsPoppedOut
    {
        get => _isPoppedOut;
        set { _isPoppedOut = value; Raise(); }
    }

    public SessionProfile? Profile { get; protected set; }
    public ITabHost? Host { get; set; }

    /// <summary>Tooltip for the tab header.</summary>
    public virtual string Description => Profile == null ? Title : $"{SessionProfile.ProtocolName(Profile.Protocol)} · {Profile.DisplayTarget}";

    /// <summary>
    /// The connection crosses the network in plain text — usually including the password — so the tab
    /// header shows an "Unencrypted" badge. FTP overrides this once it knows whether TLS was negotiated.
    /// Serial lines aren't networked and SSH/SFTP/RDP are encrypted.
    /// </summary>
    public virtual bool IsUnencrypted => Profile != null && IsCleartextProtocol(Profile.Protocol);

    public static bool IsCleartextProtocol(ProtocolKind p) =>
        p is ProtocolKind.Telnet or ProtocolKind.Rlogin or ProtocolKind.Rsh or ProtocolKind.Raw or ProtocolKind.Vnc or ProtocolKind.Xdmcp;

    /// <summary>True while closing would drop a live connection (drives "warn before closing").</summary>
    public virtual bool IsLive => State == TabState.Connected;

    public virtual bool CanPopOut => true;
    public virtual bool SupportsFullScreen => false;
    public virtual bool CanReconnect => false;
    public virtual bool CanDuplicate => Profile != null;

    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }

    /// <summary>
    /// An overlay (the connect palette) opened or closed over this tab. Tabs that host native windows
    /// (RDP, X server) hide them meanwhile: an HWND always paints above WPF content ("airspace").
    /// </summary>
    public virtual void SetOverlayOpen(bool open) { }

    /// <summary>
    /// The tab is about to move to another window (pop out, tear off, return to tabs). Tabs that host
    /// native windows which can't be re-parented safely (the RDP ActiveX control) release them here…
    /// </summary>
    public virtual void BeforeMove() { }

    /// <summary>…and recreate them here, once the tab is in its new window.</summary>
    public virtual void AfterMove() { }

    /// <summary>Last chance to veto closing (unsaved editor changes...). Warn-on-live is handled by the host.</summary>
    public virtual bool ConfirmClose(Window owner) => true;

    /// <summary>Releases the connection. Called exactly once when the tab goes away for good.</summary>
    public virtual void Shutdown() { }

    public virtual void Reconnect() { }

    /// <summary>Settings changed (fonts, colours, theme): re-apply what's relevant.</summary>
    public virtual void ApplySettings() { }

    /// <summary>Tab-specific entries for the tab header's context menu.</summary>
    public virtual IEnumerable<Control> MenuItems() => Array.Empty<Control>();

    public void RequestClose() => Host?.CloseTab(this);

    protected static MenuItem Item(string header, string glyph, Action action, bool enabled = true, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
        mi.Icon = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"], FontSize = 14 };
        mi.Click += (_, _) => action();
        return mi;
    }
}
