using System.Globalization;
using Avalonia.Data.Converters;

namespace VunLerDoc.Converters;

public sealed class NotEmptyStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string text && !string.IsNullOrWhiteSpace(text);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
