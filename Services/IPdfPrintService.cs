namespace VunLerDoc.Services;

public interface IPdfPrintService
{
    Task PrintAsync(
        IPdfDocumentService document,
        int pageCount,
        string documentName,
        PrintOptions? options = null,
        CancellationToken cancellationToken = default);
}