using CommunityToolkit.Mvvm.ComponentModel;

namespace VunLerDoc.ViewModels;

/// <summary>
/// State for a single page inside the continuous-scroll document view. Layout size is derived
/// from the page's native size in PDF points (learned once from a cheap thumbnail render) so
/// the slot never has to guess-and-jump once the full-resolution bitmap arrives. The full-
/// resolution bitmap itself is only populated while the page is on/near screen (see
/// MainWindowViewModel.RequestPageRenderAsync / ReleasePageRender) so memory stays bounded no
/// matter how many pages the document has.
/// </summary>
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

    /// <summary>What the view actually binds to: the full-resolution render when it's ready,
    /// falling back to the cheap thumbnail so something crisp-enough is always on screen
    /// instead of a blank slot while the high-res render is in flight.</summary>
    public byte[]? DisplayImage => FullImage ?? Thumbnail;

    partial void OnFullImageChanged(byte[]? value) => OnPropertyChanged(nameof(DisplayImage));
    partial void OnThumbnailChanged(byte[]? value) => OnPropertyChanged(nameof(DisplayImage));

    public PdfPageItemViewModel(int index)
    {
        Index = index;
    }

    /// <summary>Called once the thumbnail pass reports the page's true pixel size.</summary>
    public void SetNativeSize(int pixelWidth, int pixelHeight, float renderedAtDpi)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || renderedAtDpi <= 0)
            return;

        NativeWidthPoints = pixelWidth / renderedAtDpi * 72.0;
        NativeHeightPoints = pixelHeight / renderedAtDpi * 72.0;
        HasNativeSize = true;
    }

    /// <summary>
    /// Recomputes the DIP layout size for the given zoom/rotation. 96 DPI is the conventional
    /// "100%" baseline (1 PDF point == 1 DIP at zoom 1.0), matching how Avalonia's own DIP
    /// space is scaled to physical pixels by the platform for HiDPI displays.
    /// </summary>
    public void UpdateLayout(double zoom, int rotationDegrees)
    {
        var widthPoints = NativeWidthPoints;
        var heightPoints = NativeHeightPoints;
        var rotated = ((rotationDegrees % 360) + 360) % 360 is 90 or 270;
        if (rotated)
            (widthPoints, heightPoints) = (heightPoints, widthPoints);

        DisplayWidth = Math.Max(1, widthPoints / 72.0 * 96.0 * zoom);
        DisplayHeight = Math.Max(1, heightPoints / 72.0 * 96.0 * zoom);
    }
}
