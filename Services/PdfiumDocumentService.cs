using PDFiumZ;
using SkiaSharp;

namespace VunLerDoc.Services;

/// <summary>
/// PDFium is not guaranteed thread-safe for concurrent calls against the same document, so
/// every render (thumbnail or full page) is funneled through a single background gate. This
/// keeps continuous-scroll rendering (many pages potentially requested close together as the
/// user scrolls) safe without ever touching pdfium from two threads at once.
/// </summary>
public sealed class PdfiumDocumentService : IPdfDocumentService
{
    private PdfDocument? _document;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public int PageCount => _document?.PageCount ?? 0;
    public string? FilePath { get; private set; }

    public async Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await Task.Run(() => new PdfDocument(filePath), cancellationToken);
            _document?.Dispose();
            _document = document;
            FilePath = filePath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> RenderPageAsync(int pageIndex, float dpi, int rotationDegrees = 0, CancellationToken cancellationToken = default)
    {
        EnsurePageIndex(pageIndex);
        dpi = Math.Clamp(dpi, 24f, 1200f);
        var normalizedRotation = ((rotationDegrees % 360) + 360) % 360;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                var bytes = RenderAtDpi(pageIndex, dpi);
                return normalizedRotation == 0 ? bytes : RotatePng(bytes, normalizedRotation);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rotates an already-rendered page bitmap in whole 90° steps. Kept as a pixel-level
    /// rotation (rather than a UI RenderTransform) so the rotated image behaves like a normal,
    /// correctly-sized bitmap everywhere it's used (on-screen list, thumbnails, print) without
    /// any special-casing in the view layer.
    /// </summary>
    private static byte[] RotatePng(byte[] pngBytes, int degrees)
    {
        using var original = SKBitmap.Decode(pngBytes);
        var swapDims = degrees is 90 or 270;
        var width = swapDims ? original.Height : original.Width;
        var height = swapDims ? original.Width : original.Height;

        using var rotated = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-original.Width / 2f, -original.Height / 2f);
            canvas.DrawBitmap(original, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }

        using var image = SKImage.FromBitmap(rotated);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public async Task<ThumbnailResult> RenderThumbnailAsync(int pageIndex, int maxDimension, CancellationToken cancellationToken = default)
    {
        EnsurePageIndex(pageIndex);
        maxDimension = Math.Max(16, maxDimension);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                // Pass 1: a cheap low-DPI render just to learn the page's pixel aspect ratio.
                const float probeDpi = 36f;
                var probeBytes = RenderAtDpi(pageIndex, probeDpi);
                using var probeBitmap = SKBitmap.Decode(probeBytes);
                var probeWidth = probeBitmap.Width;
                var probeHeight = probeBitmap.Height;

                if (Math.Max(probeWidth, probeHeight) <= maxDimension)
                {
                    // Already small enough, no need to re-render.
                    return new ThumbnailResult(probeBytes, probeWidth, probeHeight);
                }

                // Pass 2: re-render directly at the DPI that yields the requested size —
                // DPI scales linearly with pixel size, so this lands very close to target.
                cancellationToken.ThrowIfCancellationRequested();
                var scale = maxDimension / (float)Math.Max(probeWidth, probeHeight);
                var targetDpi = Math.Clamp(probeDpi * scale, 12f, probeDpi);
                var finalBytes = RenderAtDpi(pageIndex, targetDpi);
                using var finalBitmap = SKBitmap.Decode(finalBytes);
                return new ThumbnailResult(finalBytes, finalBitmap.Width, finalBitmap.Height);
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private byte[] RenderAtDpi(int pageIndex, float dpi)
    {
        using var page = _document!.Pages[pageIndex];
        var settings = new ImageSettings
        {
            RasterDpi = dpi,
            BackgroundColor = SKColors.White,
            ImageFormat = ImageFormat.Png
        };
        return page.GenerateImage(settings);
    }

    private void EnsurePageIndex(int pageIndex)
    {
        if (_document is null)
            throw new InvalidOperationException("No PDF document is open.");
        if (pageIndex < 0 || pageIndex >= _document.PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
    }

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
        FilePath = null;
        _gate.Dispose();
    }
}
