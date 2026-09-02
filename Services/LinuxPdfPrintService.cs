using System.Diagnostics;
using System.Runtime.Versioning;

namespace VunLerDoc.Services;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class LinuxPdfPrintService : IPdfPrintService
{
    public async Task PrintAsync(
        IPdfDocumentService document,
        int pageCount,
        string documentName,
        PrintOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Este serviço de impressão é suportado apenas em Linux e macOS.");

        var filePath = document.FilePath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new InvalidOperationException("Nenhum documento aberto para imprimir.");

        var (command, arguments) = ResolvePrintCommand(filePath, documentName, options);

        var psi = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Não foi possível iniciar o comando '{command}'.");

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                ? $"'{command}' terminou com o código {process.ExitCode}."
                : stderr.Trim());
        }
    }

    private static (string Command, string[] Args) ResolvePrintCommand(string filePath, string documentName, PrintOptions? options)
    {
        var cmd = IsCommandInPath("lp") ? "lp" : IsCommandInPath("lpr") ? "lpr" : null;
        if (cmd is null)
            throw new InvalidOperationException("Nenhum comando de impressão encontrado ('lp' ou 'lpr'). Instale o CUPS.");

        var args = new List<string>();

        if (cmd == "lp")
        {
            args.Add("-t"); args.Add(documentName);
            if (!string.IsNullOrWhiteSpace(options?.PrinterName))
            {
                args.Add("-d"); args.Add(options.PrinterName);
            }
            if (options?.Copies > 1)
            {
                args.Add("-n"); args.Add(options.Copies.ToString());
            }
            if (options?.Landscape == true)
            {
                args.Add("-o"); args.Add("landscape");
            }
            args.Add(filePath);
        }
        else // lpr
        {
            args.Add("-T"); args.Add(documentName);
            if (!string.IsNullOrWhiteSpace(options?.PrinterName))
            {
                args.Add("-P"); args.Add(options.PrinterName);
            }
            if (options?.Copies > 1)
            {
                args.Add("-#"); args.Add(options.Copies.ToString());
            }
            if (options?.Landscape == true)
            {
                args.Add("-o"); args.Add("landscape");
            }
            args.Add(filePath);
        }

        return (cmd, args.ToArray());
    }

    private static bool IsCommandInPath(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("which", command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}