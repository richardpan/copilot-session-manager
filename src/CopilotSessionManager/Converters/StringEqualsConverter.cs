using System;
using System.Globalization;
using System.Windows.Data;

namespace CopilotSessionManager.Converters;

/// <summary>
/// Returns <c>true</c> when the bound string value equals the
/// <see cref="IValueConverter.Convert"/> <c>parameter</c> (case-insensitive).
/// Used for radio-style menu items that track the active theme name.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && parameter is string p
           && string.Equals(s, p, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
