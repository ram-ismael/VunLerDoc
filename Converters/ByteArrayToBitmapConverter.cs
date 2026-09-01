using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using System.IO;

namespace VunLerDoc.Converters;

public sealed class ByteArrayToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] bytes || bytes.Length == 0)
            return null;

        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
