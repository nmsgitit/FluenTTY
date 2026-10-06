using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>What the update bar needs from the main window.</summary>
public interface IUpdateHost
{
    Window HostWindow { get; }
    /// <summary>Connected sessions in every window (they end when FluenTTY restarts).</summary>
    int LiveSessionCount { get; }
    /// <summary>Closes FluenTTY (asking only about unsaved editor files) and runs <paramref name="apply"/> on the way out.</summary>
    void CloseForUpdate(Action apply);
}

/// <summary>
/// The "new version available" strip above the tabs' content. It never takes focus or pops up over what
/// you're typing: Update now → download (with progress) → one confirmation → FluenTTY restarts updated.
/// When this copy can't update itself, the main button opens the release page instead.
/// </summary>
public sealed class UpdateBar : Border
{
    private readonly IUpdateHost _host;
    private readonly TextBlock _glyph, _text;
    private readonly ProgressBar _progress;
    private readonly StackPanel _buttons;
    private Updater.ReleaseInfo? _release;
    private string? _downloaded;
    private CancellationTokenSource? _cts;

    public UpdateBar(IUpdateHost host)
    {
        _host = host;
        Visibility = Visibility.Collapsed;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 8, 8, 8);
        SetResourceReference(BackgroundProperty, "SystemFillColorAttentionBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        System.Windows.Automation.AutomationProperties.SetName(this, "Update");

        _glyph = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _text.SetResourceReference(StyleProperty, "Body");
        System.Windows.Automation.AutomationProperties.SetName(_text, "Update status");
        _progress = new ProgressBar { Width = 160, Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_glyph);
        Grid.SetColumn(_text, 1); grid.Children.Add(_text);
        Grid.SetColumn(_progress, 2); grid.Children.Add(_progress);
        Grid.SetColumn(_buttons, 3); grid.Children.Add(_buttons);
        Child = grid;
    }

    // ================================================================== states

    public void ShowAvailable(Updater.ReleaseInfo release)
    {
        _release = release;
        _downloaded = null;
        bool canInstall = Updater.CanInstall(release, out var reason);
        Set("\uE896", $"FluenTTY version {release.Number} is available. You are on version {Updater.Current.ToString(3)}.");
        if (canInstall) AddButton("Update now", UpdateNow, accent: true);
        else AddButton("Download", () => Updater.OpenReleasePage(release), accent: true, tip: reason + " The download page opens in your browser.");
        AddButton("What's new", () => WhatsNew.ShowRelease(_host.HostWindow, release));
        AddButton("Skip this version", () => { Updater.Skip(release); Hide(); }, tip: "Don't remind me about this version. Settings › Updates › Check now still finds it.");
        AddClose();
    }

    private void ShowDownloading()
    {
        Set("\uE896", $"Downloading FluenTTY {_release!.Number}…");
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        _progress.Visibility = Visibility.Visible;
        AddButton("Cancel", () => _cts?.Cancel());
    }

    private void ShowReady()
    {
        Set("\uE895", $"FluenTTY {_release!.Number} is downloaded and ready to install.");
        AddButton("Restart to update", Restart, accent: true);
        AddClose();
    }

    private void ShowError(string message)
    {
        Set("\uE783", message);
        AddButton("Try again", UpdateNow);
        AddButton("Open download page", () => Updater.OpenReleasePage(_release), accent: true);
        AddClose();
    }

    private void Hide()
    {
        _cts?.Cancel();
        Visibility = Visibility.Collapsed;
    }

    private void Set(string glyph, string text)
    {
        _glyph.Text = glyph;
        _text.Text = text;
        _progress.Visibility = Visibility.Collapsed;
        _buttons.Children.Clear();
        Visibility = Visibility.Visible;
    }

    private void AddButton(string text, Action click, bool accent = false, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
        if (accent) b.SetResourceReference(StyleProperty, "AccentButtonStyle");
        b.Click += (_, _) => click();
        _buttons.Children.Add(b);
    }

    private void AddClose()
    {
        var b = new Button { Content = "\uE711", ToolTip = "Hide until FluenTTY starts again", Margin = new Thickness(6, 0, 0, 0) };
        b.SetResourceReference(StyleProperty, "SmallIconButton");
        System.Windows.Automation.AutomationProperties.SetName(b, "Hide update");
        b.Click += (_, _) => Hide();
        _buttons.Children.Add(b);
    }

    // ================================================================== flow

    private async void UpdateNow()
    {
        if (_release == null) return;
        if (!Updater.CanInstall(_release, out _)) { Updater.OpenReleasePage(_release); return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        ShowDownloading();
        var progress = new Progress<double>(p => { _progress.IsIndeterminate = false; _progress.Value = p; _text.Text = $"Downloading FluenTTY {_release.Number}… {p:P0}"; });
        try
        {
            _downloaded = await Updater.DownloadAsync(_release, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowAvailable(_release);
            return;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"update: download failed: {ex}");
            ShowError(ex is UpdateException ? $"Couldn't update: {ex.Message}" : "Couldn't download the update. Check your connection, or get it from the download page.");
            return;
        }
        ShowReady();
        Restart();
    }

    private void Restart()
    {
        if (_release == null || _downloaded == null) return;
        int live = _host.LiveSessionCount;
        var message = $"FluenTTY will close, install version {_release.Number} and open again." +
                      (live > 0 ? $" This ends the {live} session{(live == 1 ? " that's" : "s that are")} still connected." : "") +
                      "\n\nYour settings, saved sessions and host keys stay as they are, and a backup copy is made first." +
                      (Updater.NeedsElevation ? "\n\nWindows will ask for permission, because FluenTTY is installed for all users of this PC." : "");
        if (!FluentDialog.Confirm(_host.HostWindow, "Restart to update?", message, "Restart now", "Later")) return;
        var file = _downloaded;
        var release = _release;
        _host.CloseForUpdate(() => Updater.Apply(file, release));
    }
}

/// <summary>"What's new" dialogs: after an update, from Settings/About, and for a release not installed yet.</summary>
public static class WhatsNew
{
    /// <summary>Change-log entries (newest first); after an update it offers to stop showing them.</summary>
    public static void Show(Window? owner, IReadOnlyList<Changelog.Entry> entries, bool afterUpdate)
    {
        var panel = new StackPanel();
        if (afterUpdate)
        {
            var intro = new TextBlock { Text = $"FluenTTY has been updated to version {Updater.Current.ToString(3)}.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            intro.SetResourceReference(FrameworkElement.StyleProperty, "Body");
            panel.Children.Add(intro);
        }
        var list = new StackPanel();
        foreach (var e in entries)
            AddSection(list, $"Version {e.Version.ToString(3)}" + (e.Date.Length > 0 ? $"  ·  {e.Date}" : ""), e.Items);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });

        CheckBox? keep = null;
        if (afterUpdate)
        {
            keep = new CheckBox { Content = "Show what's new after updates", IsChecked = SettingsStore.Settings.Interface.ShowWhatsNew, Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(keep);
        }
        new FluentDialog(owner, "What's new", panel, "OK", null, null).ShowDialog();
        if (keep != null && keep.IsChecked != SettingsStore.Settings.Interface.ShowWhatsNew)
        {
            SettingsStore.Settings.Interface.ShowWhatsNew = keep.IsChecked == true;
            SettingsStore.NotifySettingsChanged();
        }
    }

    /// <summary>The whole change log.</summary>
    public static void ShowAll(Window? owner) => Show(owner, Changelog.All, afterUpdate: false);

    /// <summary>Notes of a release that isn't installed yet (its GitHub release text).</summary>
    public static void ShowRelease(Window? owner, Updater.ReleaseInfo release)
    {
        var items = release.Notes.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
            .Select(l => l[2..].Replace("**", "").Trim())
            .ToList();
        var list = new StackPanel();
        AddSection(list, $"Version {release.Number}", items.Count > 0 ? items : new List<string> { "See the release page for details." });
        var page = new Button { Content = "Open the release page", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-4, 10, 0, 0) };
        page.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        page.Click += (_, _) => Updater.OpenReleasePage(release);
        list.Children.Add(page);
        new FluentDialog(owner, "What's new", new ScrollViewer { Content = list, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "OK", null, null).ShowDialog();
    }

    private static void AddSection(StackPanel list, string heading, IEnumerable<string> items)
    {
        var h = new TextBlock { Text = heading, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 6) };
        h.SetResourceReference(FrameworkElement.StyleProperty, "BodyStrong");
        list.Children.Add(h);
        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bullet = new TextBlock { Text = "•" };
            bullet.SetResourceReference(FrameworkElement.StyleProperty, "Body");
            var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(FrameworkElement.StyleProperty, "Body");
            Grid.SetColumn(text, 1);
            row.Children.Add(bullet);
            row.Children.Add(text);
            list.Children.Add(row);
        }
    }
}

/// <summary>Settings › Updates › Check now, and About › Check for updates.</summary>
public static class UpdateCheck
{
    public static async void Interactive(Window? owner)
    {
        Updater.ReleaseInfo? release;
        try
        {
            release = await Updater.CheckAsync(manual: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"update: manual check failed: {ex.Message}");
            if (new FluentDialog(owner, "Couldn't check for updates", "FluenTTY couldn't reach GitHub. Check your internet connection, or look on the download page.",
                    "Open download page", null, "Close").ShowDialog() == DialogResultKind.Primary)
                Updater.OpenReleasePage();
            return;
        }
        if (release == null)
        {
            FluentDialog.Info(owner, "You're up to date", $"You are on version {Updater.Current.ToString(3)}, the latest version of FluenTTY.");
            return;
        }
        if (Application.Current.MainWindow is MainWindow main) main.ShowUpdate(release);
        FluentDialog.Info(owner, "Update available", $"FluenTTY version {release.Number} is available. You are on version {Updater.Current.ToString(3)}.\n\nUse Update now in the bar at the top of the FluenTTY window to install it.");
    }
}
