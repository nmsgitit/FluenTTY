using System.Windows;
using System.Windows.Controls;
using FluenTTY.Connections;
using FluenTTY.Files;
using FluenTTY.Models;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>A standalone SFTP or FTP tab: the file browser full size, with edit-on-double-click.</summary>
public sealed class FileBrowserTab : SessionTab
{
    private readonly FileBrowserView _view;
    private readonly DialogConnectionUi _ui;

    public FileBrowserTab(SessionProfile profile)
    {
        Profile = profile;
        Glyph = SessionProfile.ProtocolGlyph(profile.Protocol);
        Title = string.IsNullOrEmpty(profile.Name) ? profile.DisplayTarget : profile.Name;
        _ui = new DialogConnectionUi(this, Title);
        IRemoteFileSystem fs = profile.Protocol == ProtocolKind.Ftp
            ? new FtpFileSystem(profile, SettingsStore.Settings, _ui)
            : new SftpFileSystem(profile, SettingsStore.Settings, new SshCredentials(), _ui);
        _view = new FileBrowserView(fs, compact: false);
        Content = _view;
        bool started = false;
        Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            State = TabState.Connecting;
            await _view.StartAsync();
            State = fs.IsConnected ? TabState.Connected : TabState.Disconnected;
            if (fs.IsConnected) SettingsStore.NoteRecent(profile);
        };
    }

    public override bool CanReconnect => true;

    /// <summary>FTP is unencrypted only when TLS wasn't negotiated (the header refreshes on every State change).</summary>
    public override bool IsUnencrypted => _view.FileSystem is FtpFileSystem ftp && ftp.IsConnected && !ftp.IsEncrypted;

    public override async void Reconnect()
    {
        State = TabState.Connecting;
        await _view.StartAsync();
        State = _view.FileSystem.IsConnected ? TabState.Connected : TabState.Disconnected;
    }

    public override IEnumerable<Control> MenuItems()
    {
        yield return Item("Event log", "", () => EventLogWindow.ShowFor(Window.GetWindow(this), Title, _ui.Log));
        yield return Item("Reconnect", "", Reconnect);
    }

    public override void ApplySettings() => _view.ApplySettings();

    public override void Shutdown() => _view.Shutdown();
}
