namespace VunLerDoc.Services;

/// <summary>
/// Printing abstraction so the ViewModel/UI never depends on a specific platform print stack.
/// Avalonia itself has no cross-platform print API yet, so the concrete implementation is
/// platform-specific (see WindowsPdfPrintService). Register no implementation on platforms
/// that don't have one yet — MainWindowViewModel already disables the Print button when
/// no IPdfPrintService is available.
/// </summary>
public interface IPdfPrintService
{
    Task PrintAsync(IPdfDocumentService document, int pageCount, string documentName, CancellationToken cancellationToken = default);
}
