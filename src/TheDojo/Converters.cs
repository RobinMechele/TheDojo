using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TheDojo;

/// <summary>true → Visible. "Invert" as the parameter flips it. Also treats non-empty strings, non-null objects and non-zero counts as true.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public static readonly VisibleWhenConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        if (parameter is "Invert")
        {
            truthy = !truthy;
        }

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
