using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VunLerDoc.Services;
using VunLerDoc.ViewModels;
using VunLerDoc.Views;

namespace VunLerDoc;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var pdfService = new PdfiumDocumentService();
            var printService = CreatePrintService();
            var vm = new MainWindowViewModel(pdfService, printService);
            var window = new MainWindow
            {
                DataContext = vm
            };
            desktop.MainWindow = window;

            if (desktop.Args is { Length: > 0 } args && File.Exists(args[0]) &&
                string.Equals(Path.GetExtension(args[0]), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                window.Opened += async (_, _) => await vm.OpenPdfAsync(Path.GetFullPath(args[0]));
            }

            desktop.Exit += (_, _) => pdfService.Dispose();

            // Register the "Open with" / default-apps association in the background so it
            // never delays startup. Safe and idempotent to run on every launch.
            RegisterFileAssociationInBackground();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Picks the print backend for the current OS. Returns null on platforms without
    /// one yet (macOS) — MainWindowViewModel already disables the Print button in that case.</summary>
    private static IPdfPrintService? CreatePrintService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsPdfPrintService();
        if (OperatingSystem.IsLinux()) return new LinuxPdfPrintService();
        return null;
    }

    private static void RegisterFileAssociationInBackground()
    {
        // The OperatingSystem.IsX() guards live inside the same lambda as the calls they
        // protect (rather than wrapping Task.Run itself) so the platform-compatibility
        // analyzer can actually see the guard and not flag CA1416 on code that never runs
        // on the wrong OS.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                new WindowsFileAssociationService().EnsureRegistered();
            else if (OperatingSystem.IsLinux())
                new LinuxFileAssociationService().EnsureRegistered();
        });
    }
}
