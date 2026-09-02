using CommunityToolkit.Mvvm.ComponentModel;

namespace VunLerDoc.ViewModels;

public partial class PdfPageItemViewModel : ObservableObject
{
    public int Index { get; }
    public int PageNumber => Index + 1;

    public double NativeWidthPoints { get; private set; } = 210 * 72 / 25.4;
    public double NativeHeightPoints { get; private set; } = 297 * 72 / 25.4;

    [ObservableProperty] private byte[]? _thumbnail;
    [ObservableProperty] private byte[]? _fullImage;
    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private bool _hasNativeSize;

    public byte[]? DisplayImage => FullImage ?? Thumbnail;

    partial void OnFullImageChanged(byte[]? value) => OnPropertyChanged(nameof(DisplayImage));
    partial void OnThumbnailChanged(byte[]? value) => OnPropertyChanged(nameof(DisplayImage));

    public PdfPageItemViewModel(int index)
    {
        Index = index;
    }

    public void SetNativeSize(int pixelWidth, int pixelHeight, float renderedAtDpi)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || renderedAtDpi <= 0)
            return;

        NativeWidthPoints = pixelWidth / renderedAtDpi * 72.0;
        NativeHeightPoints = pixelHeight / renderedAtDpi * 72.0;
        HasNativeSize = true;
    }

    /// <summary>
    /// Recomputes the DIP layout size for the given zoom/rotation. <paramref name="baseDpi"/>
    /// is the "100%" baseline (e.g. 120) so the layout slot matches the render resolution used
    /// by MainWindowViewModel — this keeps bitmaps and layout in sync.
    /// </summary>
    public void UpdateLayout(double zoom, int rotationDegrees, double baseDpi = 96.0)
    {
        var widthPoints = NativeWidthPoints;
        var heightPoints = NativeHeightPoints;
        var rotated = ((rotationDegrees % 360) + 360) % 360 is 90 or 270;
        if (rotated)
            (widthPoints, heightPoints) = (heightPoints, widthPoints);

        DisplayWidth = Math.Max(1, widthPoints / 72.0 * baseDpi * zoom);
        DisplayHeight = Math.Max(1, heightPoints / 72.0 * baseDpi * zoom);
    }
}