using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VunLerDoc.Services;

/// <summary>
/// Registers VunLerDoc per-user (HKEY_CURRENT_USER — no admin elevation needed) so it shows up
/// in Explorer's "Open with" picker and in Settings → Default apps for .pdf files. Uses the
/// same RegisteredApplications pattern Windows documents for well-behaved default-app
/// candidates: a ProgID with an open command, an Applications entry so "Open with" can find it
/// directly even without picking it as default, and a Capabilities block wired into
/// HKCU\Software\RegisteredApplications.
///
/// Portable/unsigned installs: Windows may still require the user to pick VunLerDoc once from
/// the "Open with" dialog before it's offered as a one-click default — that's a SmartScreen/
/// trust behavior, not something a registry write can bypass, and is expected until the app is
/// code-signed and/or shipped through an installer (MSIX gets this "for free").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileAssociationService : IFileAssociationService
{
    private const string AppRegistrationName = "VunLerDoc";
    private const string ProgId = "VunLerDoc.PdfDocument";

    public void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
                return;

            RegisterProgId(exePath);
            RegisterApplicationEntry(exePath);
            RegisterCapabilities();
            NotifyShellOfChange();
        }
        catch
        {
            // Registration is a nice-to-have convenience — never let it take the app down.
        }
    }

    private static void RegisterProgId(string exePath)
    {
        using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
        progIdKey.SetValue(null, "Documento PDF (VunLerDoc)");
        using var iconKey = progIdKey.CreateSubKey("DefaultIcon");
        iconKey.SetValue(null, $"\"{exePath}\",0");
        using var commandKey = progIdKey.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(null, $"\"{exePath}\" \"%1\"");
    }

    private static void RegisterApplicationEntry(string exePath)
    {
        var exeName = Path.GetFileName(exePath);
        using var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{exeName}");
        appKey.SetValue("FriendlyAppName", "VunLerDoc");
        using var supportedTypes = appKey.CreateSubKey("SupportedTypes");
        supportedTypes.SetValue(".pdf", string.Empty);
        using var commandKey = appKey.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(null, $"\"{exePath}\" \"%1\"");
    }

    private static void RegisterCapabilities()
    {
        using var capabilitiesKey = Registry.CurrentUser.CreateSubKey($@"Software\{AppRegistrationName}\Capabilities");
        capabilitiesKey.SetValue("ApplicationName", "VunLerDoc");
        capabilitiesKey.SetValue("ApplicationDescription", "Leitor de PDF rápido, nativo e leve.");

        using var fileAssociations = capabilitiesKey.CreateSubKey("FileAssociations");
        fileAssociations.SetValue(".pdf", ProgId);

        using var registeredApps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        registeredApps.SetValue(AppRegistrationName, $@"Software\{AppRegistrationName}\Capabilities");
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellOfChange()
    {
        const int shChangeNotifyAssocChanged = 0x08000000;
        const int shChangeNotifyFlagsIdList = 0x0000;
        SHChangeNotify(shChangeNotifyAssocChanged, shChangeNotifyFlagsIdList, IntPtr.Zero, IntPtr.Zero);
    }
}
