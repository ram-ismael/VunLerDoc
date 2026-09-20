using System.Runtime.InteropServices;
using System.Text;
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
    private IntPtr _linkDocument;
    private readonly PriorityGate _gate = new();

    public int PageCount => _document?.PageCount ?? 0;
    public string? FilePath { get; private set; }

    public async Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using (await _gate.EnterAsync(highPriority: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opened = await Task.Run(() =>
            {
                var document = new PdfDocument(filePath);
                IntPtr linkDocument = IntPtr.Zero;

                // Keep a lightweight native handle alongside PDFiumZ's high-level document.
                // It is used only for annotations/destinations/text-link metadata; page pixels
                // continue to come exclusively from the existing PDFiumZ rendering pipeline.
                // Link metadata is best-effort: an interop problem must never make an otherwise
                // valid PDF unreadable.
                try
                {
                    linkDocument = PdfiumNativeLinks.OpenDocument(filePath);
                }
                catch
                {
                    linkDocument = IntPtr.Zero;
                }

                return (Document: document, LinkDocument: linkDocument);
            }, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                if (opened.LinkDocument != IntPtr.Zero)
                    PdfiumNativeLinks.CloseDocument(opened.LinkDocument);
                opened.Document.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_linkDocument != IntPtr.Zero)
                PdfiumNativeLinks.CloseDocument(_linkDocument);
            _document?.Dispose();

            _linkDocument = opened.LinkDocument;
            _document = opened.Document;
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

    public async Task<IReadOnlyList<PdfLinkInfo>> GetPageLinksAsync(
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        using (await _gate.EnterAsync(highPriority: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePageIndex(pageIndex);

            var linkDocument = _linkDocument;
            if (linkDocument == IntPtr.Zero)
                return Array.Empty<PdfLinkInfo>();

            return await Task.Run(
                () => PdfiumNativeLinks.ReadPageLinks(linkDocument, pageIndex),
                cancellationToken);
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
        if (_linkDocument != IntPtr.Zero)
        {
            PdfiumNativeLinks.CloseDocument(_linkDocument);
            _linkDocument = IntPtr.Zero;
        }

        _document?.Dispose();
        _document = null;
        FilePath = null;
        _gate.Dispose();
    }
}

internal static class PdfiumNativeLinks
{
    private const string LibraryName = "pdfium";
    private const int DeviceScale = 100_000;
    private static readonly nuint PdfActionGoTo = 1;
    private static readonly nuint PdfActionUri = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FsRectF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    public static IntPtr OpenDocument(string filePath)
    {
        var document = FPDF_LoadDocument(filePath, IntPtr.Zero);
        if (document == IntPtr.Zero)
            throw new InvalidOperationException("PDFium não conseguiu abrir o documento para ler os links.");
        return document;
    }

    public static void CloseDocument(IntPtr document)
    {
        if (document != IntPtr.Zero)
            FPDF_CloseDocument(document);
    }

    public static IReadOnlyList<PdfLinkInfo> ReadPageLinks(IntPtr document, int pageIndex)
    {
        var page = FPDF_LoadPage(document, pageIndex);
        if (page == IntPtr.Zero)
            return Array.Empty<PdfLinkInfo>();

        try
        {
            var links = new List<PdfLinkInfo>();
            ReadAnnotationLinks(document, page, links);
            ReadDetectedWebLinks(page, links);
            return links;
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private static void ReadAnnotationLinks(IntPtr document, IntPtr page, List<PdfLinkInfo> links)
    {
        var position = 0;
        while (FPDFLink_Enumerate(page, ref position, out var link) != 0 && link != IntPtr.Zero)
        {
            if (FPDFLink_GetAnnotRect(link, out var rect) == 0)
                continue;

            int? targetPage = null;
            string? uri = null;

            var destination = FPDFLink_GetDest(document, link);
            if (destination != IntPtr.Zero)
            {
                var index = FPDFDest_GetDestPageIndex(document, destination);
                if (index >= 0)
                    targetPage = index;
            }
            else
            {
                var action = FPDFLink_GetAction(link);
                if (action != IntPtr.Zero)
                {
                    var actionType = FPDFAction_GetType(action);
                    if (actionType == PdfActionGoTo)
                    {
                        destination = FPDFAction_GetDest(document, action);
                        if (destination != IntPtr.Zero)
                        {
                            var index = FPDFDest_GetDestPageIndex(document, destination);
                            if (index >= 0)
                                targetPage = index;
                        }
                    }
                    else if (actionType == PdfActionUri)
                    {
                        uri = ReadActionUri(document, action);
                    }
                }
            }

            if (targetPage is null && string.IsNullOrWhiteSpace(uri))
                continue;

            if (TryNormalizeRect(page, rect.Left, rect.Top, rect.Right, rect.Bottom,
                    out var left, out var top, out var right, out var bottom))
            {
                AddDistinct(links, new PdfLinkInfo(left, top, right, bottom, targetPage, uri));
            }
        }
    }

    private static void ReadDetectedWebLinks(IntPtr page, List<PdfLinkInfo> links)
    {
        var textPage = FPDFText_LoadPage(page);
        if (textPage == IntPtr.Zero)
            return;

        try
        {
            var webLinks = FPDFLink_LoadWebLinks(textPage);
            if (webLinks == IntPtr.Zero)
                return;

            try
            {
                var linkCount = FPDFLink_CountWebLinks(webLinks);
                for (var linkIndex = 0; linkIndex < linkCount; linkIndex++)
                {
                    var uri = ReadDetectedUrl(webLinks, linkIndex);
                    if (string.IsNullOrWhiteSpace(uri))
                        continue;

                    var rectCount = FPDFLink_CountRects(webLinks, linkIndex);
                    for (var rectIndex = 0; rectIndex < rectCount; rectIndex++)
                    {
                        double left = 0, top = 0, right = 0, bottom = 0;
                        FPDFLink_GetRect(webLinks, linkIndex, rectIndex, ref left, ref top, ref right, ref bottom);

                        if (TryNormalizeRect(page, left, top, right, bottom,
                                out var normalizedLeft, out var normalizedTop,
                                out var normalizedRight, out var normalizedBottom))
                        {
                            AddDistinct(links, new PdfLinkInfo(
                                normalizedLeft,
                                normalizedTop,
                                normalizedRight,
                                normalizedBottom,
                                null,
                                uri));
                        }
                    }
                }
            }
            finally
            {
                FPDFLink_CloseWebLinks(webLinks);
            }
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    }

    private static bool TryNormalizeRect(
        IntPtr page,
        double left,
        double top,
        double right,
        double bottom,
        out double normalizedLeft,
        out double normalizedTop,
        out double normalizedRight,
        out double normalizedBottom)
    {
        var corners = new (double X, double Y)[]
        {
            (left, top),
            (right, top),
            (right, bottom),
            (left, bottom)
        };

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        foreach (var corner in corners)
        {
            if (FPDF_PageToDevice(
                    page,
                    0,
                    0,
                    DeviceScale,
                    DeviceScale,
                    0,
                    corner.X,
                    corner.Y,
                    out var deviceX,
                    out var deviceY) == 0)
            {
                normalizedLeft = normalizedTop = normalizedRight = normalizedBottom = 0;
                return false;
            }

            minX = Math.Min(minX, deviceX);
            minY = Math.Min(minY, deviceY);
            maxX = Math.Max(maxX, deviceX);
            maxY = Math.Max(maxY, deviceY);
        }

        normalizedLeft = Math.Clamp(minX / (double)DeviceScale, 0.0, 1.0);
        normalizedTop = Math.Clamp(minY / (double)DeviceScale, 0.0, 1.0);
        normalizedRight = Math.Clamp(maxX / (double)DeviceScale, 0.0, 1.0);
        normalizedBottom = Math.Clamp(maxY / (double)DeviceScale, 0.0, 1.0);

        return normalizedRight > normalizedLeft && normalizedBottom > normalizedTop;
    }

    private static void AddDistinct(List<PdfLinkInfo> links, PdfLinkInfo candidate)
    {
        const double epsilon = 0.0005;
        foreach (var existing in links)
        {
            if (existing.TargetPageIndex != candidate.TargetPageIndex ||
                !string.Equals(existing.Uri, candidate.Uri, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Math.Abs(existing.Left - candidate.Left) <= epsilon &&
                Math.Abs(existing.Top - candidate.Top) <= epsilon &&
                Math.Abs(existing.Right - candidate.Right) <= epsilon &&
                Math.Abs(existing.Bottom - candidate.Bottom) <= epsilon)
            {
                return;
            }
        }

        links.Add(candidate);
    }

    private static string? ReadActionUri(IntPtr document, IntPtr action)
    {
        var required = FPDFAction_GetURIPath(document, action, IntPtr.Zero, 0);
        if (required <= 1 || required > 1024 * 1024)
            return null;

        var size = checked((int)required);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var written = FPDFAction_GetURIPath(document, action, buffer, required);
            if (written <= 1)
                return null;

            var bytes = new byte[Math.Max(0, checked((int)written) - 1)];
            if (bytes.Length > 0)
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadDetectedUrl(IntPtr webLinks, int linkIndex)
    {
        var required = FPDFLink_GetURL(webLinks, linkIndex, IntPtr.Zero, 0);
        if (required <= 1 || required > 512 * 1024)
            return null;

        var buffer = Marshal.AllocHGlobal(required * sizeof(char));
        try
        {
            var written = FPDFLink_GetURL(webLinks, linkIndex, buffer, required);
            if (written <= 1)
                return null;

            return Marshal.PtrToStringUni(buffer, written - 1)?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport(LibraryName, EntryPoint = "FPDF_LoadDocument")]
    private static extern IntPtr FPDF_LoadDocument(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        IntPtr password);

    [DllImport(LibraryName)]
    private static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(LibraryName)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(LibraryName)]
    private static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(LibraryName)]
    private static extern int FPDF_PageToDevice(
        IntPtr page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        double pageX,
        double pageY,
        out int deviceX,
        out int deviceY);

    [DllImport(LibraryName)]
    private static extern int FPDFLink_Enumerate(IntPtr page, ref int startPosition, out IntPtr linkAnnotation);

    [DllImport(LibraryName)]
    private static extern int FPDFLink_GetAnnotRect(IntPtr linkAnnotation, out FsRectF rect);

    [DllImport(LibraryName)]
    private static extern IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);

    [DllImport(LibraryName)]
    private static extern IntPtr FPDFLink_GetAction(IntPtr link);

    [DllImport(LibraryName)]
    private static extern nuint FPDFAction_GetType(IntPtr action);

    [DllImport(LibraryName)]
    private static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    [DllImport(LibraryName)]
    private static extern nuint FPDFAction_GetURIPath(
        IntPtr document,
        IntPtr action,
        IntPtr buffer,
        nuint bufferLength);

    [DllImport(LibraryName)]
    private static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr destination);

    [DllImport(LibraryName)]
    private static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(LibraryName)]
    private static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(LibraryName)]
    private static extern IntPtr FPDFLink_LoadWebLinks(IntPtr textPage);

    [DllImport(LibraryName)]
    private static extern int FPDFLink_CountWebLinks(IntPtr linkPage);

    [DllImport(LibraryName)]
    private static extern int FPDFLink_GetURL(IntPtr linkPage, int linkIndex, IntPtr buffer, int bufferLength);

    [DllImport(LibraryName)]
    private static extern int FPDFLink_CountRects(IntPtr linkPage, int linkIndex);

    [DllImport(LibraryName)]
    private static extern void FPDFLink_GetRect(
        IntPtr linkPage,
        int linkIndex,
        int rectIndex,
        ref double left,
        ref double top,
        ref double right,
        ref double bottom);

    [DllImport(LibraryName)]
    private static extern void FPDFLink_CloseWebLinks(IntPtr linkPage);
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
