namespace VunLerDoc.Services;

/// <summary>
/// Result of a cheap, low-resolution page render used for thumbnails and for learning a
/// page's pixel aspect ratio before the full-resolution render is available.
/// </summary>
public readonly record struct ThumbnailResult(byte[] Bytes, int PixelWidth, int PixelHeight);

public interface IPdfDocumentService : IDisposable
{
    Task OpenAsync(string filePath, CancellationToken cancellationToken = default);

    int PageCount { get; }
    string? FilePath { get; }

    /// <summary>
    /// Re-rasterizes the page's actual vector content at <paramref name="dpi"/> via PDFium.
    /// Every call produces a fresh, pixel-accurate bitmap for that exact resolution — never a
    /// stretched copy of a previously cached bitmap — so text and line art stay crisp at any
    /// zoom level. This is how every production PDF viewer (Chrome, Acrobat, Preview) works:
    /// PDF is fundamentally re-rasterized on zoom, there's no "pure vector" on-screen display.
    /// <paramref name="rotationDegrees"/> must be one of 0/90/180/270.
    /// </summary>
    Task<byte[]> RenderPageAsync(int pageIndex, float dpi, int rotationDegrees = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same full-quality PDFium rasterization as <see cref="RenderPageAsync"/>, but queued at
    /// background priority. Visible/on-demand pages always jump ahead of this work, which lets
    /// the reader progressively prepare the rest of a document without making navigation wait.
    /// </summary>
    Task<byte[]> RenderPageBackgroundAsync(int pageIndex, float dpi, int rotationDegrees = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders a small preview (longest side capped at <paramref name="maxDimension"/> pixels)
    /// used for the thumbnail rail and to learn the page's aspect ratio up front, so the
    /// continuous-scroll layout can reserve the correct slot height before the full-resolution
    /// image lands (no layout jump/flicker while scrolling).
    /// </summary>
    Task<ThumbnailResult> RenderThumbnailAsync(int pageIndex, int maxDimension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads clickable PDF link annotations plus automatically detected web links for a page.
    /// Bounds are normalized to the unzoomed/unrotated page bitmap so zoom never requires a
    /// second PDF parse/render pass. Internal destinations are returned as zero-based pages.
    /// </summary>
    Task<IReadOnlyList<PdfLinkInfo>> GetPageLinksAsync(int pageIndex, CancellationToken cancellationToken = default);
}
