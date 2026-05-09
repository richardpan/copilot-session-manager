using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CopilotSessionManager.Converters;

/// <summary>
/// Maps non-null reference values to <see cref="Visibility.Visible"/> and
/// nulls to <see cref="Visibility.Collapsed"/>. Useful for hiding panels
/// whose <c>DataContext</c> is itself bound to a nullable property.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
