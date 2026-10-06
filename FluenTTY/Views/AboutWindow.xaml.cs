using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FluenTTY.Services;

namespace FluenTTY.Views;

/// <summary>About, Licenses and AI disclosure — the same three sections and nav as FluentPlay's About dialog.</summary>
public partial class AboutWindow : Window
{
    private sealed record Section(string Glyph, string Title, FrameworkElement Pane);

    private static AboutWindow? _open;
    private bool _navReady;

    public static void ShowSingle(Window? owner)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new AboutWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    public AboutWindow()
    {
        InitializeComponent();
        ThemeService.Attach(this);

        VersionText.Text = $"Version {AppInfo.Version} · {AppInfo.Flavour}";
        CopyrightText.Text = LicenseCatalog.AppCopyright.Replace("(c)", "©");
        PrivacyText.Text = Statements.Privacy.Trim();
        InstallText.Text = $"{(AppInfo.IsPortable ? "Portable copy — nothing is installed and nothing is written to the registry." : "Installed copy.")}\nProgram: {AppInfo.ExePath}\nData: {AppInfo.DataDirectory}" +
                           (AppInfo.DataDirectoryIsFallback ? "\n(The folder next to the program isn't writable, so data is kept in your profile instead.)" : "");
        AiTitle.Text = Statements.AiDisclosureTitle;
        AiText.Text = Statements.AiDisclosure.Trim();

        var components = new List<LicenseEntry> { LicenseCatalog.App };
        components.AddRange(LicenseCatalog.ThirdParty);
        ComponentBox.ItemsSource = components;
        ComponentBox.SelectedIndex = 0;

        foreach (var pane in new FrameworkElement[] { AboutPane, LicensesPane, AiPane })
            pane.RenderTransform = new TranslateTransform();

        Nav.ItemsSource = new[]
        {
            new Section("", "About", AboutPane),
            new Section("", "Licenses", LicensesPane),
            new Section("", "AI disclosure", AiPane),
        };
        Nav.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            MoveIndicator(animate: false);
            _navReady = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (Section s in Nav.Items) s.Pane.Visibility = Visibility.Collapsed;
        if (Nav.SelectedItem is not Section sel) return;
        sel.Pane.Visibility = Visibility.Visible;
        if (!_navReady) return;
        MoveIndicator(animate: true);
        var duration = ThemeService.Anim(320);
        sel.Pane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
        if (sel.Pane.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, duration) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    private void MoveIndicator(bool animate)
    {
        if (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) is not ListBoxItem item) return;
        item.UpdateLayout();
        var top = item.TranslatePoint(new Point(0, 0), Nav).Y;
        double y = top + (item.ActualHeight - NavIndicator.Height) / 2;
        if (!animate)
        {
            NavIndicatorOffset.BeginAnimation(TranslateTransform.YProperty, null);
            NavIndicatorOffset.Y = y;
            return;
        }
        NavIndicatorOffset.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, ThemeService.Anim(260)) { EasingFunction = ThemeService.EaseOut }, HandoffBehavior.SnapshotAndReplace);
    }

    private void Component_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComponentBox.SelectedItem is not LicenseEntry entry) return;
        ComponentNote.Text = string.IsNullOrEmpty(entry.Note) ? entry.Copyright : $"{entry.Note}\n{entry.Copyright}";
        ComponentUrl.Text = entry.Url;
        ComponentUrl.Visibility = string.IsNullOrEmpty(entry.Url) ? Visibility.Collapsed : Visibility.Visible;
        LicenseText.Text = entry.Text();
        LicenseText.ScrollToHome();
    }

    private void ComponentUrl_Click(object sender, MouseButtonEventArgs e)
    {
        var url = ComponentUrl.Text;
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) => WhatsNew.ShowAll(this);

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => UpdateCheck.Interactive(this);

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.DataDirectory) { UseShellExecute = true }); } catch { }
    }
}
