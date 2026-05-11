using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CopilotSessionManager.Converters;

/// <summary>
/// Inverse of WPF's built-in <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>:
/// maps <c>true</c> to <see cref="Visibility.Collapsed"/> and <c>false</c> to
/// <see cref="Visibility.Visible"/>. Used by the inline-rename UX (#105) so
/// the static title text is hidden while the editor TextBox is shown.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v != Visibility.Visible;
        }
        return false;
    }
}
