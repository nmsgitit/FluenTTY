using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluenTTY.Services;
using FluenTTY.Terminal;

namespace FluenTTY.Views;

/// <summary>PuTTY's Event Log: connection steps, algorithms, forwarding and errors for one session.</summary>
public sealed class EventLogWindow : Window
{
    public static void ShowFor(Window? owner, string title, IReadOnlyList<string> lines)
    {
        var w = new EventLogWindow(title, lines) { Owner = owner };
        w.Show();
    }

    private EventLogWindow(string title, IReadOnlyList<string> lines)
    {
        Title = $"Event log — {title}";
        Width = 760;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        ThemeService.Attach(this);

        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(16, 16, 16, 8),
        };
        var copy = new Button { Content = "Copy", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => TerminalCanvas.TrySetClipboard(box.Text);
        var close = new Button { Content = "Close", Padding = new Thickness(16, 5, 16, 5), IsCancel = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 16) };
        buttons.Children.Add(copy);
        buttons.Children.Add(close);

        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(box);
        Content = dock;
        Loaded += (_, _) => box.ScrollToEnd();
    }
}
