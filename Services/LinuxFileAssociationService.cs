using System.Diagnostics;
using System.Runtime.Versioning;

namespace VunLerDoc.Services;

/// <summary>
/// Registers VunLerDoc per-user (no root needed) so it shows up in the "Open With" list of
/// GNOME Files/Nautilus, Dolphin, Thunar, etc. Follows the freedesktop.org Desktop Entry spec:
/// drop a .desktop file declaring the application/pdf MIME type into
/// ~/.local/share/applications, then ask the desktop environment to refresh its cache.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxFileAssociationService : IFileAssociationService
{
    private const string DesktopFileName = "vunlerdoc.desktop";

    public void EnsureRegistered()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
                return;

            var appsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "applications");
            Directory.CreateDirectory(appsDir);

            var desktopFilePath = Path.Combine(appsDir, DesktopFileName);
            var desktopEntry =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=VunLerDoc\n" +
                "Comment=Leitor de PDF rápido e nativo\n" +
                $"Exec=\"{exePath}\" %f\n" +
                "Terminal=false\n" +
                "MimeType=application/pdf;\n" +
                "Categories=Office;Viewer;\n" +
                "NoDisplay=false\n";

            // Skip the write (and the desktop-database refresh) if nothing actually changed —
            // this runs on every startup and should be a cheap no-op after the first launch.
            if (!File.Exists(desktopFilePath) || File.ReadAllText(desktopFilePath) != desktopEntry)
            {
                File.WriteAllText(desktopFilePath, desktopEntry);
                TryRun("update-desktop-database", appsDir);
            }
        }
        catch
        {
            // Registration is a nice-to-have convenience — never let it take the app down.
        }
    }

    private static void TryRun(string fileName, string argument)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            process?.WaitForExit(2000);
        }
        catch
        {
            // update-desktop-database may not be installed on every distro; the .desktop file
            // alone is still enough for most file managers to pick up on their next scan.
        }
    }
}
