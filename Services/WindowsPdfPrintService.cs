using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace VunLerDoc.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsPdfPrintService : IPdfPrintService
{
    private const float PrintDpi = 300f;

    public async Task PrintAsync(
        IPdfDocumentService document,
        int pageCount,
        string documentName,
        PrintOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A impressão está disponível apenas no Windows.");

        if (pageCount <= 0)
            throw new InvalidOperationException("O documento não contém páginas para imprimir.");

        if (PrinterSettings.InstalledPrinters.Count == 0)
            throw new InvalidOperationException("Nenhuma impressora instalada foi encontrada no sistema.");

        cancellationToken.ThrowIfCancellationRequested();

        var printerName = options?.PrinterName;
        if (string.IsNullOrWhiteSpace(printerName) ||
            !PrinterSettings.InstalledPrinters.Cast<string>().Contains(printerName))
        {
            var defaultSettings = new PrinterSettings();
            printerName = defaultSettings.PrinterName;
        }

        var printDocument = new PrintDocument
        {
            DocumentName = documentName,
            PrinterSettings = new PrinterSettings { PrinterName = printerName! },
            PrintController = new StandardPrintController()
        };

        if (options?.Copies > 1)
            printDocument.PrinterSettings.Copies = (short)Math.Min(options.Copies, 99);

        if (options?.Landscape == true)
            printDocument.DefaultPageSettings.Landscape = true;

        int pageIndex = 0;
        Exception? capturedException = null;

        printDocument.BeginPrint += (_, _) => { pageIndex = 0; };

        printDocument.PrintPage += (_, e) =>
        {
            if (pageIndex >= pageCount || cancellationToken.IsCancellationRequested)
            {
                e.HasMorePages = false;
                return;
            }

            try
            {
                var bytes = Task.Run(async () =>
                    await document.RenderPageAsync(pageIndex, PrintDpi, rotationDegrees: 0, cancellationToken))
                    .GetAwaiter().GetResult();

                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);

                var bounds = e.MarginBounds;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    bounds = e.PageBounds;

                var pageScale = Math.Min(
                    bounds.Width / (float)image.Width,
                    bounds.Height / (float)image.Height);

                var drawW = image.Width * pageScale;
                var drawH = image.Height * pageScale;
                var x = bounds.X + (bounds.Width - drawW) / 2f;
                var y = bounds.Y + (bounds.Height - drawH) / 2f;

                e.Graphics!.DrawImage(image, x, y, drawW, drawH);

                pageIndex++;
                e.HasMorePages = pageIndex < pageCount;
            }
            catch (Exception ex)
            {
                capturedException = ex;
                e.HasMorePages = false;
                e.Cancel = true;
            }
        };

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            printDocument.Print();
        }, cancellationToken);

        if (capturedException is not null)
        {
            throw new InvalidOperationException(
                $"Erro ao imprimir a página {pageIndex + 1}: {capturedException.Message}",
                capturedException);
        }
    }
}