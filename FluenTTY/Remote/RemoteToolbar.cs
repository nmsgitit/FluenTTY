using System.Windows;
using System.Windows.Controls;
using FluenTTY.Views;

namespace FluenTTY.Remote;

/// <summary>Toolbar shared by the RDP and VNC tabs: status on the left, actions on the right.</summary>
public sealed class RemoteToolbar : Border
{
    private readonly TextBlock _status;
    private readonly StackPanel _buttons;

    public RemoteToolbar()
    {
        Padding = new Thickness(8, 4, 8, 4);
        BorderThickness = new Thickness(0, 0, 0, 1);
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        // Buttons keep their room on the right; the status text gets what's left and trims.
        var dock = new DockPanel();
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(StyleProperty, "Caption");
        _buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(_buttons, Dock.Right);
        dock.Children.Add(_buttons);
        dock.Children.Add(_status);
        Child = dock;
    }

    /// <summary>One line in the toolbar (multi-line errors from the RDP control would stretch it); full text in the tooltip.</summary>
    public string Status
    {
        set
        {
            _status.Text = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s*[\r\n]+\s*", " ").Trim();
            _status.ToolTip = value;
        }
    }

    public Button Add(string text, string glyph, string tip, Action action)
    {
        var b = new Button { Content = text, Tag = glyph, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "ToolbarButton");
        b.Click += (_, _) => action();
        _buttons.Children.Add(b);
        return b;
    }

    /// <summary>Full screen for a remote tab: the pop-out window's full screen, or the main window's.</summary>
    public static void ToggleFullScreen(SessionTab tab)
    {
        switch (Window.GetWindow(tab))
        {
            case PopOutWindow pop: pop.ToggleFullScreen(); break;
            case MainWindow main: main.ToggleFullScreen(); break;
        }
    }

    /// <summary>Hides a tab's "Pop out" button while it is already in its own window.</summary>
    public static void HideWhenPoppedOut(SessionTab tab, Button popOut)
    {
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionTab.IsPoppedOut))
                popOut.Dispatcher.BeginInvoke(() => popOut.Visibility = tab.IsPoppedOut ? Visibility.Collapsed : Visibility.Visible);
        };
    }

    public static void PopOut(SessionTab tab)
    {
        if (Window.GetWindow(tab) is MainWindow main) main.PopOut(tab);
    }
}
