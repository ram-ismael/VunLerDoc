using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using VunLerDoc.Services;

namespace VunLerDoc.ViewModels;

public partial class PdfPageItemViewModel : ObservableObject
{
    public int Index { get; }
    public int PageNumber => Index + 1;

    public double NativeWidthPoints { get; private set; } = 210 * 72 / 25.4;
    public double NativeHeightPoints { get; private set; } = 297 * 72 / 25.4;

    // Bitmaps are decoded once (off the UI thread, see MainWindowViewModel) and bound directly,
    // instead of raw byte[] re-decoded on every binding pass through an IValueConverter.
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private Bitmap? _fullImage;
    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
    [ObservableProperty] private double _unrotatedDisplayWidth;
    [ObservableProperty] private double _unrotatedDisplayHeight;
    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private bool _hasNativeSize;

    private IReadOnlyList<PdfLinkInfo> _links = Array.Empty<PdfLinkInfo>();
    public bool LinksLoaded { get; private set; }

    /// <summary>Full-quality render when ready, otherwise the low-res thumbnail.</summary>
    public Bitmap? DisplayImage => FullImage ?? Thumbnail;
    public bool HasDisplayImage => DisplayImage is not null;
    public bool IsPlaceholderVisible => !HasDisplayImage;
    public bool IsThumbnailPlaceholderVisible => Thumbnail is null;

    partial void OnFullImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(DisplayImage));
        OnPropertyChanged(nameof(HasDisplayImage));
        OnPropertyChanged(nameof(IsPlaceholderVisible));
    }

    partial void OnThumbnailChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(DisplayImage));
        OnPropertyChanged(nameof(HasDisplayImage));
        OnPropertyChanged(nameof(IsPlaceholderVisible));
        OnPropertyChanged(nameof(IsThumbnailPlaceholderVisible));
    }

    public PdfPageItemViewModel(int index)
    {
        Index = index;
    }

    public void SetLinks(IReadOnlyList<PdfLinkInfo> links)
    {
        _links = links ?? Array.Empty<PdfLinkInfo>();
        LinksLoaded = true;
    }

    /// <summary>
    /// Hit-tests a point in the page's currently displayed (possibly user-rotated) rectangle.
    /// Link bounds themselves stay normalized to the original page bitmap, so zoom and rotation
    /// never require PDFium to parse or rasterize the page again.
    /// </summary>
    public bool TryGetLinkAtDisplayPoint(double x, double y, out PdfLinkInfo? link)
    {
        link = null;
        if (!LinksLoaded || _links.Count == 0 || DisplayWidth <= 0 || DisplayHeight <= 0 ||
            UnrotatedDisplayWidth <= 0 || UnrotatedDisplayHeight <= 0)
        {
            return false;
        }

        var rotation = ((RotationDegrees % 360) + 360) % 360;
        double normalizedX;
        double normalizedY;

        switch (rotation)
        {
            case 90:
                normalizedX = y / UnrotatedDisplayWidth;
                normalizedY = 1.0 - (x / UnrotatedDisplayHeight);
                break;
            case 180:
                normalizedX = 1.0 - (x / UnrotatedDisplayWidth);
                normalizedY = 1.0 - (y / UnrotatedDisplayHeight);
                break;
            case 270:
                normalizedX = 1.0 - (y / UnrotatedDisplayWidth);
                normalizedY = x / UnrotatedDisplayHeight;
                break;
            default:
                normalizedX = x / UnrotatedDisplayWidth;
                normalizedY = y / UnrotatedDisplayHeight;
                break;
        }

        normalizedX = Math.Clamp(normalizedX, 0.0, 1.0);
        normalizedY = Math.Clamp(normalizedY, 0.0, 1.0);

        // A tiny display-space tolerance makes thin text links pleasant to click without making
        // neighbouring index entries overlap at normal zoom levels.
        var toleranceX = 2.0 / Math.Max(1.0, UnrotatedDisplayWidth);
        var toleranceY = 2.0 / Math.Max(1.0, UnrotatedDisplayHeight);

        foreach (var candidate in _links)
        {
            if (normalizedX >= candidate.Left - toleranceX &&
                normalizedX <= candidate.Right + toleranceX &&
                normalizedY >= candidate.Top - toleranceY &&
                normalizedY <= candidate.Bottom + toleranceY)
            {
                link = candidate;
                return true;
            }
        }

        return false;
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
        var normalizedRotation = ((rotationDegrees % 360) + 360) % 360;
        RotationDegrees = normalizedRotation;

        // Keep the source image's dimensions independent of rotation. The view rotates this
        // already-loaded bitmap around its centre; only the outer page slot swaps width/height.
        UnrotatedDisplayWidth = Math.Max(1, NativeWidthPoints / 72.0 * baseDpi * zoom);
        UnrotatedDisplayHeight = Math.Max(1, NativeHeightPoints / 72.0 * baseDpi * zoom);

        if (normalizedRotation is 90 or 270)
        {
            DisplayWidth = UnrotatedDisplayHeight;
            DisplayHeight = UnrotatedDisplayWidth;
        }
        else
        {
            DisplayWidth = UnrotatedDisplayWidth;
            DisplayHeight = UnrotatedDisplayHeight;
        }
    }
}
