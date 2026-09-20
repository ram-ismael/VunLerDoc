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
    private readonly PriorityGate _gate = new();

    public int PageCount => _document?.PageCount ?? 0;
    public string? FilePath { get; private set; }

    public async Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using (await _gate.EnterAsync(highPriority: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await Task.Run(() => new PdfDocument(filePath), cancellationToken);
            _document?.Dispose();
            _document = document;
            FilePath = filePath;
        }
    }

    /// <summary>
    /// Full-quality on-screen/print render. Always high priority: this is what the user is
    /// actually looking at (or printing), so it must never queue behind background thumbnail
    /// generation - see <see cref="PriorityGate"/>.
    /// </summary>
    public Task<byte[]> RenderPageAsync(int pageIndex, float dpi, int rotationDegrees = 0, CancellationToken cancellationToken = default)
        => RenderPageCoreAsync(pageIndex, dpi, rotationDegrees, highPriority: true, cancellationToken);

    public Task<byte[]> RenderPageBackgroundAsync(int pageIndex, float dpi, int rotationDegrees = 0, CancellationToken cancellationToken = default)
        => RenderPageCoreAsync(pageIndex, dpi, rotationDegrees, highPriority: false, cancellationToken);

    private async Task<byte[]> RenderPageCoreAsync(
        int pageIndex,
        float dpi,
        int rotationDegrees,
        bool highPriority,
        CancellationToken cancellationToken)
    {
        EnsurePageIndex(pageIndex);
        dpi = Math.Clamp(dpi, 24f, 1200f);
        var normalizedRotation = ((rotationDegrees % 360) + 360) % 360;

        using (await _gate.EnterAsync(highPriority, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                var bytes = RenderAtDpi(pageIndex, dpi);
                return normalizedRotation == 0 ? bytes : RotatePng(bytes, normalizedRotation);
            }, cancellationToken);
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

    /// <summary>
    /// Cheap low-res render for the sidebar rail and initial aspect-ratio probing. Always low
    /// priority so it can never delay the full-quality render of whatever page is actually
    /// on screen - see <see cref="PriorityGate"/>.
    /// </summary>
    public async Task<ThumbnailResult> RenderThumbnailAsync(int pageIndex, int maxDimension, CancellationToken cancellationToken = default)
    {
        EnsurePageIndex(pageIndex);
        maxDimension = Math.Max(16, maxDimension);

        using (await _gate.EnterAsync(highPriority: false, cancellationToken))
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

/// <summary>
/// A single-slot mutex where "high priority" waiters always get the gate before "low
/// priority" waiters, regardless of arrival order. Used to keep pdfium calls serialized
/// (required - it isn't thread-safe) while guaranteeing the page the user is actually
/// looking at is never stuck in line behind background thumbnail generation.
/// </summary>
internal sealed class PriorityGate : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _highPriorityWaiters = new();
    private readonly Queue<TaskCompletionSource> _lowPriorityWaiters = new();
    private bool _isHeld;
    private bool _isDisposed;

    public async Task<IDisposable> EnterAsync(bool highPriority, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (!_isHeld)
            {
                _isHeld = true;
                tcs.TrySetResult();
            }
            else
            {
                (highPriority ? _highPriorityWaiters : _lowPriorityWaiters).Enqueue(tcs);
            }
        }

        using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            await tcs.Task.ConfigureAwait(false);
        }

        return new Releaser(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            // High-priority waiters (on-screen/print renders) always jump the queue ahead of
            // low-priority ones (background thumbnails), no matter which arrived first.
            while (true)
            {
                var next = _highPriorityWaiters.Count > 0 ? _highPriorityWaiters.Dequeue()
                         : _lowPriorityWaiters.Count > 0 ? _lowPriorityWaiters.Dequeue()
                         : null;

                if (next is null)
                {
                    _isHeld = false;
                    return;
                }

                // Ownership passes directly to 'next' - gate stays held, just changes hands.
                // If 'next' was already cancelled while queued, TrySetResult fails - try the
                // next candidate instead of leaving the gate stuck "held" with no owner.
                if (next.TrySetResult())
                    return;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
            foreach (var waiter in _highPriorityWaiters) waiter.TrySetCanceled();
            foreach (var waiter in _lowPriorityWaiters) waiter.TrySetCanceled();
            _highPriorityWaiters.Clear();
            _lowPriorityWaiters.Clear();
        }
    }

    private sealed class Releaser(PriorityGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
