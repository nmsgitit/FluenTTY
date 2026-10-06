using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FluenTTY.Models;

namespace FluenTTY.Views;

/// <summary>One header in the title-bar tab strip (icon + state dot, title, bell marker, close button).</summary>
public sealed class TabHeader : Border
{
    public SessionTab Tab { get; }
    private readonly TextBlock _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _alert;
    private readonly TextBlock _insecure;
    private readonly Ellipse _dot;
    private readonly Grid _iconGrid;
    private readonly Button _close;
    private readonly Border _hover;
    private bool _selected;

    public event Action<TabHeader>? CloseClicked;

    public TabHeader(SessionTab tab)
    {
        Tab = tab;
        Height = BarHeight;
        Padding = Normal;
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
        Focusable = false;
        ClipToBounds = true;

        // Same shape as the selection plate (MainWindow insets it by PlateInset on each side).
        _hover = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(PlateInset - 10, 0, PlateInset - 4, 0), Opacity = 0, IsHitTestVisible = false };
        _hover.SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");

        _icon = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        _icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _dot = new Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -2) };
        _iconGrid = new Grid { Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        _iconGrid.Children.Add(_icon);
        _iconGrid.Children.Add(_dot);

        _title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        _alert = new TextBlock { Text = "", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
        _alert.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _alert.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");

        // Open padlock for cleartext protocols (Telnet, Rlogin, Rsh, raw, VNC, XDMCP, FTP without TLS).
        _insecure = new TextBlock
        {
            Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed,
            ToolTip = "Unencrypted connection: what you type (including passwords) and what the server sends can be read by anyone on the network path.",
        };
        _insecure.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _insecure.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        System.Windows.Automation.AutomationProperties.SetName(_insecure, "Unencrypted connection");

        _close = new Button { Content = "", Width = 24, Height = 24, FontSize = 9, Margin = new Thickness(4, 0, 0, 0), Focusable = false, ToolTip = "Close tab (Ctrl+Shift+W)" };
        _close.SetResourceReference(StyleProperty, "SmallIconButton");
        _close.Width = 24; _close.Height = 24; _close.FontSize = 9;
        System.Windows.Automation.AutomationProperties.SetName(_close, "Close tab");
        _close.Click += (_, _) => CloseClicked?.Invoke(this);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_hover);
        Grid.SetColumnSpan(_hover, 5);
        grid.Children.Add(_iconGrid);
        Grid.SetColumn(_title, 1); grid.Children.Add(_title);
        Grid.SetColumn(_insecure, 2); grid.Children.Add(_insecure);
        Grid.SetColumn(_alert, 3); grid.Children.Add(_alert);
        Grid.SetColumn(_close, 4); grid.Children.Add(_close);
        Child = grid;

        System.Windows.Automation.AutomationProperties.SetName(this, tab.Title);
        tab.PropertyChanged += OnTabChanged;
        MouseEnter += (_, _) => UpdateVisuals();
        MouseLeave += (_, _) => UpdateVisuals();
        SizeChanged += (_, _) => UpdateVisuals();
        Refresh();
    }

    /// <summary>Height of every title-bar control (tab headers and buttons) so they share one centre line.</summary>
    public const double BarHeight = 32;

    /// <summary>Gap between a header's edge and its hover / selection plate, so neighbouring plates never touch.</summary>
    public const double PlateInset = 2;

    // Widths of the parts that sit beside the title (margins included).
    private const double IconPart = 13 + 9, BadgePart = 6 + 12, ClosePart = 4 + 24;

    /// <summary>Below this width a tab is "compact": like Chrome and Firefox, its close button only shows on hover or when selected.</summary>
    private const double CompactWidth = 90;

    /// <summary>Narrowest a tab gets before the strip scrolls instead: still room for the close button and its hover plate.</summary>
    public const double MinWidthForClose = 36;

    private static readonly Thickness Normal = new(10, 0, 4, 0);

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; UpdateVisuals(); }
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    public void Refresh()
    {
        _icon.Text = Tab.Glyph;
        _title.Text = Tab.Title;
        ToolTip = Tab.IsUnencrypted ? Tab.Description + " · unencrypted" : Tab.Description;
        System.Windows.Automation.AutomationProperties.SetName(this, Tab.Title);
        switch (Tab.State)
        {
            case TabState.Connecting: _dot.SetResourceReference(Shape.FillProperty, "SystemFillColorCautionBrush"); _dot.Visibility = Visibility.Visible; break;
            case TabState.Connected: _dot.SetResourceReference(Shape.FillProperty, "SystemFillColorSuccessBrush"); _dot.Visibility = Tab is StartTab or Files.EditorTab ? Visibility.Collapsed : Visibility.Visible; break;
            case TabState.Disconnected: _dot.SetResourceReference(Shape.FillProperty, "SystemFillColorCriticalBrush"); _dot.Visibility = Visibility.Visible; break;
            default: _dot.Visibility = Visibility.Collapsed; break;
        }
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        _title.SetResourceReference(TextBlock.ForegroundProperty, _selected || IsMouseOver ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        _icon.SetResourceReference(TextBlock.ForegroundProperty, _selected ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        _hover.Opacity = !_selected && IsMouseOver ? 1 : 0;
        var mode = Services.SettingsStore.Settings.Interface.TabCloseButtons;
        double width = double.IsNaN(Width) ? ActualWidth : Width;
        bool compact = width > 0 && width < CompactWidth;
        bool active = _selected || IsMouseOver;
        _close.Visibility = mode switch
        {
            TabCloseButtons.Never => Visibility.Collapsed,
            // Narrow tabs (many open) keep the close button for the tab under the mouse and the selected one.
            _ when compact => active ? Visibility.Visible : Visibility.Collapsed,
            TabCloseButtons.Hover => active ? Visibility.Visible : Visibility.Hidden,
            _ => Visibility.Visible,
        };

        // Tiny tabs (lots open, as in Chrome) show just one thing, centred: the close button when hovered or
        // selected, otherwise the icon.
        bool closeShown = _close.Visibility != Visibility.Collapsed;
        bool tiny = width > 0 && width < Normal.Left + Normal.Right + IconPart + ClosePart;
        if (tiny)
        {
            double side = Math.Max(0, Math.Floor((width - (closeShown ? 24 : 13)) / 2));
            Padding = new Thickness(side, 0, side, 0);
            _close.Margin = new Thickness(0);
            _iconGrid.Margin = new Thickness(0);
        }
        else
        {
            Padding = Normal;
            _close.Margin = new Thickness(4, 0, 0, 0);
            _iconGrid.Margin = new Thickness(0, 0, 9, 0);
        }
        _hover.Margin = new Thickness(PlateInset - Padding.Left, 0, PlateInset - Padding.Right, 0);

        // The close button wins over the icon and badges when space runs out, so it is never clipped.
        bool insecure = Tab.IsUnencrypted, alert = Tab.HasAlert && !_selected;
        double room = width <= 0 ? double.MaxValue
            : tiny ? (closeShown ? 0 : IconPart)
            : width - Padding.Left - Padding.Right - (closeShown ? ClosePart : 0);
        bool showIcon = Take(IconPart);
        _iconGrid.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
        _insecure.Visibility = insecure && Take(BadgePart) ? Visibility.Visible : Visibility.Collapsed;
        _alert.Visibility = alert && Take(BadgePart) ? Visibility.Visible : Visibility.Collapsed;
        // What's left is the title's; below a few characters it would only draw "…".
        _title.Visibility = tiny || room < 20 ? Visibility.Collapsed : Visibility.Visible;

        bool Take(double part)
        {
            if (room < part) return false;
            room -= part;
            return true;
        }
    }

    public void Detach() => Tab.PropertyChanged -= OnTabChanged;
}
