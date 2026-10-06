using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluenTTY.Models;
using FluenTTY.Services;
using FluenTTY.Terminal;
using Microsoft.Win32;

namespace FluenTTY.Views;

/// <summary>New / edit session dialog: connection fields per protocol plus per-session overrides.</summary>
public sealed class SessionEditorWindow : Window
{
    public readonly record struct EditResult(SessionProfile? Profile, bool Save, bool Connect);

    public static EditResult Edit(Window? owner, SessionProfile profile, bool isNew)
    {
        var w = new SessionEditorWindow(profile, isNew) { Owner = owner };
        w.ShowDialog();
        return w._result;
    }

    private readonly SessionProfile _p;
    private EditResult _result;
    private readonly StackPanel _fields = new();
    private readonly ComboBox _protocol = new();
    private readonly TextBox _name = new(), _folder = new(), _host = new(), _port = new(), _user = new(), _key = new(), _domain = new(),
        _command = new(), _termType = new(), _fontSize = new(), _keepalive = new(), _initial = new(), _notes = new(), _width = new(), _height = new();
    private readonly PasswordBox _password = new();
    private readonly CheckBox _savePassword = new() { Content = "Save password (encrypted for your Windows account)" };
    private readonly ComboBox _serialLine = new() { IsEditable = true }, _speed = new() { IsEditable = true }, _dataBits = new(), _stopBits = new(),
        _parity = new(), _flow = new(), _scheme = new(), _charset = new(), _log = new(), _ftpEnc = new(), _rdpSize = new(), _xdmcp = new();
    private readonly CheckBox _useProxy = new() { Content = "Use the proxy from Settings" }, _sftp = new() { Content = "Open the SFTP browser beside the terminal", IsThreeState = true },
        _compression = new() { Content = "Enable compression", IsThreeState = true }, _admin = new() { Content = "Connect to the admin (console) session" },
        _viewOnly = new() { Content = "View only (don't send keyboard or mouse)" };
    private readonly ObservableCollection<PortForward> _forwards = new();
    private readonly ObservableCollection<LoginStep> _login = new();

    private SessionEditorWindow(SessionProfile profile, bool isNew)
    {
        _p = profile;
        Title = isNew ? "New session" : $"Edit session — {profile.Name}";
        Width = 640;
        Height = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ThemeService.Attach(this);

        foreach (var pk in StartTab.Protocols) _protocol.Items.Add(new ComboBoxItem { Content = SessionProfile.ProtocolName(pk), Tag = pk });
        _protocol.SelectedIndex = Math.Max(0, Array.IndexOf(StartTab.Protocols, profile.Protocol));
        _protocol.SelectionChanged += (_, _) => BuildFields();

        _name.Text = profile.Name;
        _folder.Text = profile.Folder;
        _host.Text = profile.Host;
        _port.Text = profile.Port > 0 ? profile.Port.ToString() : "";
        _user.Text = profile.Username;
        var saved = SecretStore.Unprotect(profile.Password);
        _password.Password = saved ?? "";
        _savePassword.IsChecked = saved != null;
        _key.Text = profile.PrivateKeyFile;
        _domain.Text = profile.Domain;
        _command.Text = profile.RshCommand ?? "";
        _termType.Text = profile.TerminalType ?? "";
        _fontSize.Text = profile.FontSize?.ToString() ?? "";
        _keepalive.Text = profile.KeepaliveSeconds?.ToString() ?? "";
        _initial.Text = profile.InitialCommand ?? "";
        _initial.AcceptsReturn = true; _initial.MinHeight = 54; _initial.TextWrapping = TextWrapping.NoWrap;
        _initial.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _notes.Text = profile.Notes;
        _notes.AcceptsReturn = true; _notes.MinHeight = 54; _notes.TextWrapping = TextWrapping.Wrap;
        _useProxy.IsChecked = profile.UseProxy;
        _sftp.IsChecked = profile.OpenSftpBrowser;
        _compression.IsChecked = profile.Compression;
        _admin.IsChecked = profile.RdpAdminSession == true;
        _viewOnly.IsChecked = profile.VncViewOnly == true;
        _width.Text = profile.RdpWidth?.ToString() ?? "";
        _height.Text = profile.RdpHeight?.ToString() ?? "";
        foreach (var f in profile.PortForwards) _forwards.Add(f);
        foreach (var s in profile.LoginScript) _login.Add(s);

        try { foreach (var port in SerialPort.GetPortNames().Distinct().OrderBy(n => n.Length).ThenBy(n => n)) _serialLine.Items.Add(port); } catch { }
        _serialLine.Text = profile.Protocol == ProtocolKind.Serial ? profile.Host : SettingsStore.Settings.Serial.Line;
        foreach (var s in new[] { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 }) _speed.Items.Add(s.ToString());
        _speed.Text = (profile.SerialSpeed ?? SettingsStore.Settings.Serial.Speed).ToString();
        Fill(_dataBits, new[] { "5", "6", "7", "8" }, (profile.SerialDataBits ?? SettingsStore.Settings.Serial.DataBits).ToString());
        FillEnum(_stopBits, profile.SerialStopBits ?? SettingsStore.Settings.Serial.StopBits);
        FillEnum(_parity, profile.SerialParity ?? SettingsStore.Settings.Serial.Parity);
        FillEnum(_flow, profile.SerialFlow ?? SettingsStore.Settings.Serial.FlowControl);
        _scheme.Items.Add("Default (Settings)");
        foreach (var s in SettingsStore.AllSchemes) _scheme.Items.Add(s.Name);
        _scheme.SelectedItem = profile.ColorScheme ?? "Default (Settings)";
        if (_scheme.SelectedIndex < 0) _scheme.SelectedIndex = 0;
        _charset.Items.Add("Default (Settings)");
        foreach (var c in TerminalConfig.Charsets) _charset.Items.Add(c);
        _charset.SelectedItem = profile.RemoteCharset ?? "Default (Settings)";
        if (_charset.SelectedIndex < 0) _charset.SelectedIndex = 0;
        Fill(_log, new[] { "Default (Settings)", "Off", "Printable output", "All session output" },
            profile.LogMode switch { LogMode.Off => "Off", LogMode.Printable => "Printable output", LogMode.AllOutput => "All session output", _ => "Default (Settings)" });
        Fill(_ftpEnc, new[] { "Default (Settings)", "Auto", "None", "Explicit TLS", "Implicit TLS" },
            profile.FtpEncryption switch { FtpEncryption.Auto => "Auto", FtpEncryption.None => "None", FtpEncryption.Explicit => "Explicit TLS", FtpEncryption.Implicit => "Implicit TLS", _ => "Default (Settings)" });
        Fill(_rdpSize, new[] { "Default (Settings)", "Fit to the window", "Fixed size" },
            profile.RdpSizeMode switch { RdpSizeMode.FitToWindow => "Fit to the window", RdpSizeMode.Fixed => "Fixed size", _ => "Default (Settings)" });
        Fill(_xdmcp, new[] { "Default (Settings)", "Query", "Broadcast", "Indirect" }, profile.XdmcpQuery?.ToString() ?? "Default (Settings)");

        // Layout
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Margin = new Thickness(24, 16, 24, 16), Children = { _fields } } };
        var footer = new Border { Padding = new Thickness(24, 14, 24, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var saveConnect = new Button { Content = "Save and connect", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        saveConnect.SetResourceReference(StyleProperty, "AccentButtonStyle");
        saveConnect.Click += (_, _) => Finish(save: true, connect: true);
        var save = new Button { Content = "Save", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Finish(save: true, connect: false);
        var connectOnly = new Button { Content = "Connect without saving", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        connectOnly.Click += (_, _) => Finish(save: false, connect: true);
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(saveConnect);
        buttons.Children.Add(save);
        buttons.Children.Add(connectOnly);
        buttons.Children.Add(cancel);
        footer.Child = buttons;
        var dock = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        Content = dock;

        BuildFields();
        Loaded += (_, _) => (string.IsNullOrEmpty(_host.Text) ? _host : _name).Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private ProtocolKind Protocol => (ProtocolKind)((ComboBoxItem)_protocol.SelectedItem).Tag;

    private static void Fill(ComboBox box, IEnumerable<string> items, string selected)
    {
        foreach (var i in items) box.Items.Add(i);
        box.SelectedItem = selected;
        if (box.SelectedIndex < 0) box.SelectedIndex = 0;
    }

    private static void FillEnum<T>(ComboBox box, T selected) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>()) box.Items.Add(v);
        box.SelectedItem = selected;
    }

    // ------------------------------------------------------------------ layout helpers

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 10, 0, 4) };
        t.SetResourceReference(StyleProperty, "Body");
        t.FontSize = 13;
        return t;
    }

    private void Row(string label, FrameworkElement control, string? help = null)
    {
        _fields.Children.Add(Label(label));
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        _fields.Children.Add(control);
        if (help != null)
        {
            var h = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            h.SetResourceReference(StyleProperty, "Caption");
            _fields.Children.Add(h);
        }
    }

    private void Pair(string l1, FrameworkElement c1, string l2, FrameworkElement c2, double secondWidth = 120)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(secondWidth) });
        var a = new StackPanel { Children = { Label(l1), c1 } };
        var b = new StackPanel { Children = { Label(l2), c2 } };
        System.Windows.Automation.AutomationProperties.SetName(c1, l1);
        System.Windows.Automation.AutomationProperties.SetName(c2, l2);
        Grid.SetColumn(b, 2);
        g.Children.Add(a);
        g.Children.Add(b);
        _fields.Children.Add(g);
    }

    private void Section(string title)
    {
        var t = new TextBlock { Text = title };
        t.SetResourceReference(StyleProperty, "GroupHeader");
        _fields.Children.Add(t);
    }

    private static void ClearDeep(Panel panel)
    {
        foreach (var child in panel.Children.OfType<UIElement>().ToList())
            if (child is Panel p && !ReferenceEquals(p, _dummy)) ClearDeep(p);
        panel.Children.Clear();
    }

    private static readonly Panel? _dummy = null;

    private static void Detach(UIElement e)
    {
        if (e is FrameworkElement { Parent: Panel p }) p.Children.Remove(e);
    }

    private void BuildFields()
    {
        // Controls are reused between rebuilds, so every panel they sat in must let go of them first.
        ClearDeep(_fields);
        var proto = Protocol;

        Row("Protocol", _protocol);
        Pair("Session name", _name, "Folder", _folder, 200);

        Section("Connection");
        switch (proto)
        {
            case ProtocolKind.Serial:
                Pair("Serial line", _serialLine, "Speed (baud)", _speed, 160);
                var g = new UniformGrid4(_dataBits, _stopBits, _parity, _flow, "Data bits", "Stop bits", "Parity", "Flow control");
                _fields.Children.Add(g);
                break;
            default:
                Pair("Host name or IP address", _host, "Port", _port, 110);
                string portHint = $"Leave the port empty for the default ({SessionProfile.DefaultPort(proto)}).";
                if (proto == ProtocolKind.Vnc) portHint = "VNC display :N is port 5900+N (e.g. :1 = 5901). Leave empty for 5900.";
                var hint = new TextBlock { Text = portHint, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
                hint.SetResourceReference(StyleProperty, "Caption");
                _fields.Children.Add(hint);
                break;
        }

        if (proto is not (ProtocolKind.Serial or ProtocolKind.Raw or ProtocolKind.Xdmcp or ProtocolKind.Vnc))
        {
            if (proto == ProtocolKind.Rdp) Pair("User name", _user, "Domain", _domain, 200);
            else Row("User name", _user, "Leave empty to be asked when connecting.");
        }
        if (proto is ProtocolKind.Ssh or ProtocolKind.Sftp or ProtocolKind.Ftp or ProtocolKind.Rdp or ProtocolKind.Vnc or ProtocolKind.Telnet)
        {
            Row("Password", _password, "Optional. Without a saved password FluenTTY asks when connecting.");
            _savePassword.Margin = new Thickness(0, 6, 0, 0);
            _fields.Children.Add(_savePassword);
        }
        if (proto is ProtocolKind.Ssh or ProtocolKind.Sftp)
        {
            var keyRow = new DockPanel();
            var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            browse.Click += (_, _) =>
            {
                var dlg = new OpenFileDialog { Filter = "Private keys (*.ppk;id_*;*.pem;*.key)|*.ppk;id_*;*.pem;*.key|All files|*.*", Title = "Private key file" };
                if (dlg.ShowDialog(this) == true) _key.Text = dlg.FileName;
            };
            DockPanel.SetDock(browse, Dock.Right);
            keyRow.Children.Add(browse);
            keyRow.Children.Add(_key);
            Row("Private key file", keyRow, "PuTTY (.ppk v2/v3), OpenSSH and PEM keys are supported. Leave empty to use the default key from Settings › SSH.");
        }
        if (proto == ProtocolKind.Rsh) Row("Command", _command, "The command rsh runs on the server.");
        if (proto == ProtocolKind.Ftp) Row("Encryption", _ftpEnc);
        if (proto == ProtocolKind.Rdp)
        {
            Row("Desktop size", _rdpSize);
            Pair("Width", _width, "Height", _height, 200);
            _fields.Children.Add(_admin);
        }
        if (proto == ProtocolKind.Vnc) { _viewOnly.Margin = new Thickness(0, 8, 0, 0); _fields.Children.Add(_viewOnly); }
        if (proto == ProtocolKind.Xdmcp) Row("XDMCP query", _xdmcp, "Query asks this host for a login screen; Broadcast finds hosts on the local network; Indirect asks this host to pick one.");

        if (SessionProfile.IsTerminal(proto))
        {
            Section("Terminal");
            Pair("Colour scheme", _scheme, "Font size (pt)", _fontSize, 150);
            Pair("Remote character set", _charset, "Terminal type", _termType, 200);
            Row("Session logging", _log);
            Row("Initial commands", _initial, "Typed after login, one line at a time (PuTTY's \"Remote command\", PuTTY CM's post-login commands).");
            BuildLoginScript();
        }

        if (proto is ProtocolKind.Ssh or ProtocolKind.Telnet or ProtocolKind.Rlogin or ProtocolKind.Raw or ProtocolKind.Sftp or ProtocolKind.Ftp or ProtocolKind.Vnc)
        {
            Section("Network");
            if (proto != ProtocolKind.Ftp && proto != ProtocolKind.Vnc) Row("Keepalive interval (seconds)", _keepalive, "Empty uses Settings › Connection; 0 turns keepalives off.");
            _fields.Children.Add(_useProxy);
        }
        if (proto == ProtocolKind.Ssh)
        {
            _sftp.Margin = new Thickness(0, 8, 0, 0);
            _fields.Children.Add(_sftp);
            _compression.Margin = new Thickness(0, 8, 0, 0);
            _fields.Children.Add(_compression);
            var tri = new TextBlock { Text = "A dimmed box means \"use the setting from Settings\".", Margin = new Thickness(0, 3, 0, 0) };
            tri.SetResourceReference(StyleProperty, "Caption");
            _fields.Children.Add(tri);
            BuildForwards();
        }

        Section("Notes");
        _fields.Children.Add(_notes);
    }

    private void BuildLoginScript()
    {
        Section("Login script");
        var help = new TextBlock
        {
            Text = "Waits for text matching a pattern (regular expression) and types a reply. {username} and {password} are filled in from this session. Useful for Telnet and serial devices that ask for a login.",
            TextWrapping = TextWrapping.Wrap,
        };
        help.SetResourceReference(StyleProperty, "Caption");
        _fields.Children.Add(help);
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Render()
        {
            list.Children.Clear();
            foreach (var step in _login.ToList())
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var expect = new TextBox { Text = step.Expect, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Expect (regular expression)" };
                expect.TextChanged += (_, _) => step.Expect = expect.Text;
                var send = new TextBox { Text = step.Send, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Send" };
                send.TextChanged += (_, _) => step.Send = send.Text;
                var secret = new CheckBox { Content = "Secret", IsChecked = step.Secret, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                secret.Checked += (_, _) => step.Secret = true;
                secret.Unchecked += (_, _) => step.Secret = false;
                var remove = new Button { Content = "", ToolTip = "Remove step" };
                remove.SetResourceReference(StyleProperty, "SmallIconButton");
                remove.Click += (_, _) => { _login.Remove(step); Render(); };
                Grid.SetColumn(send, 1); Grid.SetColumn(secret, 2); Grid.SetColumn(remove, 3);
                g.Children.Add(expect); g.Children.Add(send); g.Children.Add(secret); g.Children.Add(remove);
                list.Children.Add(g);
            }
        }
        Render();
        _fields.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var add = new Button { Content = "Add step", Tag = "" };
        add.SetResourceReference(StyleProperty, "ToolbarButton");
        add.Click += (_, _) => { _login.Add(new LoginStep()); Render(); };
        var preset = new Button { Content = "Add user name + password prompts", Tag = "" };
        preset.SetResourceReference(StyleProperty, "ToolbarButton");
        preset.Click += (_, _) => { foreach (var s in LoginStep.UsernamePassword()) _login.Add(s); Render(); };
        buttons.Children.Add(add);
        buttons.Children.Add(preset);
        _fields.Children.Add(buttons);
    }

    private void BuildForwards()
    {
        Section("Port forwarding (tunnels)");
        var list = new ListBox { MinHeight = 60, MaxHeight = 140, ItemsSource = _forwards };
        _fields.Children.Add(list);
        var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        var kind = new ComboBox { Width = 110 };
        kind.Items.Add("Local"); kind.Items.Add("Remote"); kind.Items.Add("Dynamic");
        kind.SelectedIndex = 0;
        var src = new TextBox { Width = 90, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Source port (optionally address:port)" };
        var dst = new TextBox { Margin = new Thickness(6, 0, 0, 0), ToolTip = "Destination host:port" };
        var add = new Button { Content = "Add", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        System.Windows.Automation.AutomationProperties.SetName(src, "Source port");
        System.Windows.Automation.AutomationProperties.SetName(dst, "Destination");
        add.Click += (_, _) =>
        {
            char k = kind.SelectedIndex switch { 1 => 'R', 2 => 'D', _ => 'L' };
            var pf = Services.PuttyImport.ParseForward(k == 'D' ? $"D{src.Text.Trim()}" : $"{k}{src.Text.Trim()}={dst.Text.Trim()}");
            if (pf == null) { FluentDialog.Info(this, "Port forwarding", "Enter a source port and, for Local/Remote, a destination as host:port."); return; }
            _forwards.Add(pf);
            src.Clear(); dst.Clear();
        };
        remove.Click += (_, _) => { if (list.SelectedItem is PortForward pf) _forwards.Remove(pf); };
        var sp = new DockPanel();
        DockPanel.SetDock(kind, Dock.Left); DockPanel.SetDock(src, Dock.Left); DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(add, Dock.Right);
        sp.Children.Add(kind); sp.Children.Add(src); sp.Children.Add(remove); sp.Children.Add(add); sp.Children.Add(dst);
        _fields.Children.Add(sp);
        var help = new TextBlock { Text = "Local: a port on this PC reaches destination through the server. Remote: a port on the server reaches destination through this PC. Dynamic: a SOCKS proxy on this PC.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        help.SetResourceReference(StyleProperty, "Caption");
        _fields.Children.Add(help);
    }

    private void Finish(bool save, bool connect)
    {
        var proto = Protocol;
        string host = proto == ProtocolKind.Serial ? _serialLine.Text.Trim() : _host.Text.Trim();
        if (host.Length == 0)
        {
            FluentDialog.Info(this, "Missing host", proto == ProtocolKind.Serial ? "Choose a serial line (for example COM3)." : "Enter the host name or IP address to connect to.");
            return;
        }
        int port = 0;
        if (_port.Text.Trim().Length > 0 && (!int.TryParse(_port.Text.Trim(), out port) || port < 1 || port > 65535))
        {
            FluentDialog.Info(this, "Port", "The port must be a number from 1 to 65535.");
            return;
        }

        var p = _p;
        p.Protocol = proto;
        p.Host = host;
        p.Port = port;
        p.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : (string.IsNullOrEmpty(_user.Text) ? host : $"{_user.Text.Trim()}@{host}");
        p.Folder = _folder.Text.Trim().Trim('/');
        p.Username = _user.Text.Trim();
        p.Password = _savePassword.IsChecked == true && _password.Password.Length > 0 ? SecretStore.Protect(_password.Password) : null;
        p.PrivateKeyFile = _key.Text.Trim();
        p.Domain = _domain.Text.Trim();
        p.RshCommand = string.IsNullOrWhiteSpace(_command.Text) ? null : _command.Text;
        p.TerminalType = string.IsNullOrWhiteSpace(_termType.Text) ? null : _termType.Text.Trim();
        p.FontSize = double.TryParse(_fontSize.Text, out var fs) && fs > 4 ? fs : null;
        p.KeepaliveSeconds = int.TryParse(_keepalive.Text, out var ka) && ka >= 0 ? ka : null;
        p.InitialCommand = string.IsNullOrWhiteSpace(_initial.Text) ? null : _initial.Text;
        p.Notes = _notes.Text;
        p.UseProxy = _useProxy.IsChecked == true;
        p.OpenSftpBrowser = _sftp.IsChecked;
        p.Compression = _compression.IsChecked;
        p.ColorScheme = _scheme.SelectedIndex > 0 ? _scheme.SelectedItem as string : null;
        p.RemoteCharset = _charset.SelectedIndex > 0 ? _charset.SelectedItem as string : null;
        p.LogMode = _log.SelectedIndex switch { 1 => LogMode.Off, 2 => LogMode.Printable, 3 => LogMode.AllOutput, _ => null };
        p.PortForwards = _forwards.ToList();
        p.LoginScript = _login.Where(s => !string.IsNullOrWhiteSpace(s.Expect)).ToList();
        if (proto == ProtocolKind.Serial)
        {
            p.SerialSpeed = int.TryParse(_speed.Text, out var sp) ? sp : null;
            p.SerialDataBits = int.TryParse(_dataBits.SelectedItem as string, out var db) ? db : null;
            p.SerialStopBits = (SerialStopBits?)_stopBits.SelectedItem;
            p.SerialParity = (SerialParity?)_parity.SelectedItem;
            p.SerialFlow = (SerialFlow?)_flow.SelectedItem;
        }
        p.FtpEncryption = _ftpEnc.SelectedIndex switch { 1 => FtpEncryption.Auto, 2 => FtpEncryption.None, 3 => FtpEncryption.Explicit, 4 => FtpEncryption.Implicit, _ => null };
        p.RdpAdminSession = _admin.IsChecked == true ? true : null;
        p.RdpSizeMode = _rdpSize.SelectedIndex switch { 1 => RdpSizeMode.FitToWindow, 2 => RdpSizeMode.Fixed, _ => null };
        p.RdpWidth = int.TryParse(_width.Text, out var w) && w > 0 ? w : null;
        p.RdpHeight = int.TryParse(_height.Text, out var h) && h > 0 ? h : null;
        p.VncViewOnly = _viewOnly.IsChecked == true ? true : null;
        p.XdmcpQuery = _xdmcp.SelectedIndex switch { 1 => XdmcpQuery.Query, 2 => XdmcpQuery.Broadcast, 3 => XdmcpQuery.Indirect, _ => null };

        // Connect-without-saving still carries a typed (unsaved) password for this connection.
        if (!save && _password.Password.Length > 0) p.Password = SecretStore.Protect(_password.Password);
        _result = new EditResult(p, save, connect);
        Close();
    }

    /// <summary>Four labelled combo boxes in a row (serial line settings).</summary>
    private sealed class UniformGrid4 : Grid
    {
        public UniformGrid4(ComboBox a, ComboBox b, ComboBox c, ComboBox d, string la, string lb, string lc, string ld)
        {
            for (int i = 0; i < 4; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var items = new[] { (a, la), (b, lb), (c, lc), (d, ld) };
            for (int i = 0; i < 4; i++)
            {
                Detach(items[i].Item1);
                var sp = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0) };
                sp.Children.Add(Label(items[i].Item2));
                sp.Children.Add(items[i].Item1);
                System.Windows.Automation.AutomationProperties.SetName(items[i].Item1, items[i].Item2);
                SetColumn(sp, i);
                Children.Add(sp);
            }
        }
    }
}
