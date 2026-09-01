using System.Globalization;
using Avalonia.Data.Converters;

namespace VunLerDoc.Converters;

public sealed class BooleanNegationConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool boolean && !boolean;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
