using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FluenTTY.Services;

namespace FluenTTY.Views;

public enum DialogResultKind { None, Primary, Secondary }

/// <summary>
/// WinUI ContentDialog look-alike: title, body (text or any content), an accent primary button, an
/// optional secondary button and a close button. Modal to its owner.
/// </summary>
public sealed class FluentDialog : Window
{
    private DialogResultKind _result = DialogResultKind.None;
    private readonly StackPanel _body;
    private readonly Button _primary;
    private readonly Button? _secondary;
    private readonly Button? _close;

    public FluentDialog(Window? owner, string title, object? content, string primary, string? secondary = null, string? close = "Cancel",
        bool primaryIsAccent = true, bool destructive = false)
    {
        Title = title;
        Owner = owner;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = owner == null;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 480;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ThemeService.Attach(this);
        UseLayoutRounding = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _body = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        heading.SetResourceReference(StyleProperty, "SectionTitle");
        _body.Children.Add(heading);
        if (content is string s)
        {
            var tb = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(StyleProperty, "Body");
            _body.Children.Add(tb);
        }
        else if (content is UIElement el) _body.Children.Add(el);
        root.Children.Add(_body);

        var footer = new Border { Padding = new Thickness(24), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Grid.SetRow(footer, 1);
        var buttons = new UniformGrid { Rows = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        footer.Child = buttons;
        root.Children.Add(footer);

        _primary = new Button { Content = primary, Margin = new Thickness(0, 0, 4, 0), IsDefault = true, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (primaryIsAccent) _primary.SetResourceReference(StyleProperty, "AccentButtonStyle");
        if (destructive)
        {
            _primary.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
            _primary.Foreground = Brushes.White;
        }
        _primary.Click += (_, _) => { _result = DialogResultKind.Primary; if (Validate?.Invoke() != false) Close(); else _result = DialogResultKind.None; };
        buttons.Children.Add(_primary);
        AutomationProperties_SetName(_primary, primary);

        if (secondary != null)
        {
            _secondary = new Button { Content = secondary, Margin = new Thickness(4, 0, 4, 0), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            _secondary.Click += (_, _) => { _result = DialogResultKind.Secondary; Close(); };
            buttons.Children.Add(_secondary);
        }
        if (close != null)
        {
            var closeButton = new Button { Content = close, Margin = new Thickness(4, 0, 0, 0), IsCancel = true, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            closeButton.Click += (_, _) => { _result = DialogResultKind.None; Close(); };
            buttons.Children.Add(closeButton);
            _close = closeButton;
        }

        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = DialogResultKind.None; Close(); } };
    }

    private static void AutomationProperties_SetName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);

    /// <summary>Called when the primary button is clicked; return false to keep the dialog open.</summary>
    public Func<bool>? Validate { get; set; }

    public void FocusPrimary() => Loaded += (_, _) => _primary.Focus();

    /// <summary>
    /// For dangerous confirmations: Enter means Cancel (the close button gets focus and IsDefault), and
    /// the primary/secondary buttons stay disabled for <paramref name="armAfter"/> so keys typed just
    /// before the dialog appeared can't confirm it.
    /// </summary>
    public void MakeCancelDefault(TimeSpan armAfter)
    {
        if (_close == null) return;
        _primary.IsDefault = false;
        _close.IsDefault = true;
        _primary.IsEnabled = false;
        if (_secondary != null) _secondary.IsEnabled = false;
        Loaded += (_, _) =>
        {
            _close.Focus();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = armAfter };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _primary.IsEnabled = true;
                if (_secondary != null) _secondary.IsEnabled = true;
            };
            timer.Start();
        };
    }

    public new DialogResultKind ShowDialog()
    {
        base.ShowDialog();
        return _result;
    }

    // ------------------------------------------------------------------ helpers

    public static bool Confirm(Window? owner, string title, string message, string yes = "OK", string no = "Cancel", bool destructive = false)
        => new FluentDialog(owner, title, message, yes, null, no, destructive: destructive).ShowDialog() == DialogResultKind.Primary;

    public static void Info(Window? owner, string title, string message)
        => new FluentDialog(owner, title, message, "OK", null, null).ShowDialog();

    public static string? PromptText(Window? owner, string title, string label, string initial = "", bool password = false)
    {
        var panel = new StackPanel();
        var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        lbl.SetResourceReference(StyleProperty, "Body");
        panel.Children.Add(lbl);
        Control box;
        if (password) box = new PasswordBox { Password = initial };
        else box = new TextBox { Text = initial };
        System.Windows.Automation.AutomationProperties.SetName(box, label.TrimEnd(':').Trim());
        panel.Children.Add(box);
        var dlg = new FluentDialog(owner, title, panel, "OK");
        dlg.Loaded += (_, _) =>
        {
            box.Focus();
            if (box is TextBox t) t.SelectAll();
        };
        if (dlg.ShowDialog() != DialogResultKind.Primary) return null;
        return box is PasswordBox pb ? pb.Password : ((TextBox)box).Text;
    }
}
