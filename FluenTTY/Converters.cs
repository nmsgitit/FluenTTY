using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FluenTTY;

public sealed class VisibleWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            null => false,
            _ => true,
        };
        if (Invert) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value!;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : value;
}
