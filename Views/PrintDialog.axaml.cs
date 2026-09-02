using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using VunLerDoc.Services;

#if WINDOWS
using System.Drawing.Printing;
#endif

namespace VunLerDoc.Views;

public partial class PrintDialog : Window
{
    public PrintDialog()
    {
        InitializeComponent();

        if (OperatingSystem.IsWindows())
        {
#if WINDOWS
            LoadWindowsPrinters();
#else
            SetPrinterError("Impressão não disponível nesta compilação. Use TFM net8.0-windows.");
#endif
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            LoadCupsPrinters();
        }
        else
        {
            SetPrinterError("Impressão não disponível neste sistema");
        }
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private void LoadWindowsPrinters()
    {
        try
        {
            foreach (string printer in PrinterSettings.InstalledPrinters.Cast<string>())
            {
                PrinterCombo.Items.Add(printer);
            }

            var settings = new PrinterSettings();
            if (PrinterCombo.Items.Count > 0)
            {
                PrinterCombo.SelectedItem = settings.PrinterName;
            }
        }
        catch
        {
            // Silenciar falhas de acesso a impressoras
        }
    }
#endif

    private void LoadCupsPrinters()
    {
        try
        {
            var psi = new ProcessStartInfo("lpstat", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                SetPrinterError("Não foi possível executar lpstat");
                return;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var printers = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct()
                .ToList();

            foreach (var printer in printers)
            {
                PrinterCombo.Items.Add(printer);
            }

            if (PrinterCombo.Items.Count > 0)
            {
                PrinterCombo.SelectedIndex = 0;
            }
            else
            {
                SetPrinterError("Nenhuma impressora encontrada");
            }
        }
        catch (Exception ex)
        {
            SetPrinterError($"Erro: {ex.Message}");
        }
    }

    private void SetPrinterError(string message)
    {
        PrinterCombo.IsEnabled = false;
        PrinterCombo.PlaceholderText = message;
    }

    public PrintOptions? Result { get; private set; }
    public bool IsCancelled { get; private set; } = true;

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        IsCancelled = true;
        Result = null;
        Close();
    }

    private void PrintClick(object? sender, RoutedEventArgs e)
    {
        IsCancelled = false;
        Result = new PrintOptions
        {
            PrinterName = PrinterCombo.SelectedItem?.ToString() ?? string.Empty,
            Copies = (int)(CopiesUpDown.Value ?? 1),
            Landscape = LandscapeCheck.IsChecked ?? false
        };
        Close();
    }
}