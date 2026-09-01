using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace VunLerDoc.Services;

/// <summary>
/// Printing via System.Drawing.Printing (Windows print spooler). Every page is rendered
/// natively through pdfium at print resolution (300 DPI) — not scaled up from the on-screen
/// preview bitmap — before the print job starts, so print output quality is independent of
/// whatever zoom level the document happened to be at on screen.
///
/// This currently prints straight to the default printer. Swapping in a printer/page-range
/// picker means either a small custom Avalonia dialog listing
/// <see cref="PrinterSettings.InstalledPrinters"/>, or (Windows-only build) a
/// System.Windows.Forms.PrintDialog — the latter needs the project to multi-target
/// `net10.0-windows` with UseWindowsForms, which this project intentionally avoids to keep a
/// single cross-platform TargetFramework.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPdfPrintService : IPdfPrintService
{
    private const float PrintDpi = 300f;

    public async Task PrintAsync(IPdfDocumentService document, int pageCount, string documentName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A impressão está disponível apenas no Windows nesta versão.");

        if (pageCount <= 0)
            return;

        // Pre-render every page before handing control to the print pipeline: PrintPage fires
        // synchronously per page and shouldn't block on async pdfium calls.
        var renderedPages = new byte[pageCount][];
        for (var i = 0; i < pageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            renderedPages[i] = await document.RenderPageAsync(i, PrintDpi, rotationDegrees: 0, cancellationToken);
        }

        var printDocument = new PrintDocument { DocumentName = documentName };
        var pageIndex = 0;

        printDocument.PrintPage += (_, e) =>
        {
            if (pageIndex >= renderedPages.Length || e.Graphics is null)
            {
                e.HasMorePages = false;
                return;
            }

            using var stream = new MemoryStream(renderedPages[pageIndex]);
            using var image = Image.FromStream(stream);

            var bounds = e.MarginBounds;
            var scale = Math.Min(bounds.Width / (float)image.Width, bounds.Height / (float)image.Height);
            var drawWidth = image.Width * scale;
            var drawHeight = image.Height * scale;
            var x = bounds.X + (bounds.Width - drawWidth) / 2f;
            var y = bounds.Y + (bounds.Height - drawHeight) / 2f;

            e.Graphics.DrawImage(image, x, y, drawWidth, drawHeight);

            pageIndex++;
            e.HasMorePages = pageIndex < renderedPages.Length;
        };

        printDocument.Print();
    }
}
