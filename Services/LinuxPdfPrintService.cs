using System.Diagnostics;
using System.Runtime.Versioning;

namespace VunLerDoc.Services;

/// <summary>
/// Printing via CUPS' `lp` command. Unlike the Windows path, this hands the *original* PDF
/// straight to the print spooler instead of rasterizing pages ourselves — CUPS understands PDF
/// natively, so this is even higher fidelity than the Windows GDI+ path, and needs no bitmap
/// rendering step at all. Requires CUPS (`lp`) to be installed, which is the default print
/// stack on effectively every desktop Linux distribution.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPdfPrintService : IPdfPrintService
{
    public async Task PrintAsync(IPdfDocumentService document, int pageCount, string documentName, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Este serviço de impressão está disponível apenas no Linux.");

        var filePath = document.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new InvalidOperationException("Nenhum documento aberto para imprimir.");

        var psi = new ProcessStartInfo("lp")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(documentName);
        psi.ArgumentList.Add(filePath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Não foi possível iniciar o comando 'lp'. Confirma que o CUPS está instalado.");

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"'lp' terminou com o código {process.ExitCode}." : stderr.Trim());
        }
    }
}
