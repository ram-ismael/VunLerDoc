<p align="center">
  <img src="docs/logo.png" alt="VunLerDoc Logo" width="140" height="140" />
</p>

<h1 align="center">VunLerDoc 📄</h1>

<p align="center">
  <b>A fast, native, dependency-free PDF reader for the desktop.</b><br/>
  Open a document. Scroll smoothly through hundreds of pages. Print it. Done.
</p>

<p align="center">
  <a href="https://github.com/ram-ismael/VunLerDoc/stargazers"><img src="https://img.shields.io/github/stars/ram-ismael/VunLerDoc?style=for-the-badge&color=yellow" alt="Stars"></a>
  <a href="https://github.com/ram-ismael/VunLerDoc/network/members"><img src="https://img.shields.io/github/forks/ram-ismael/VunLerDoc?style=for-the-badge&color=blue" alt="Forks"></a>
  <a href="https://github.com/ram-ismael/VunLerDoc/releases"><img src="https://img.shields.io/badge/version-1.0.0-blue.svg?style=for-the-badge" alt="Version"></a>
  <img src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux-brightgreen.svg?style=for-the-badge" alt="Platform">
  <img src="https://img.shields.io/badge/.NET-10-512BD4.svg?style=for-the-badge" alt=".NET 10">
  <img src="https://img.shields.io/badge/license-MIT-orange.svg?style=for-the-badge" alt="License">
</p>

<p align="center">
  <a href="#-download--run">Download</a> ·
  <a href="#-features">Features</a> ·
  <a href="#-tech-stack">Tech Stack</a> ·
  <a href="#-building-from-source-developers">Build</a> ·
  <a href="#-project-structure">Structure</a>
</p>

---

Most PDF readers are either a browser tab away from your data, or a bloated installer full of things you didn't ask for. **VunLerDoc** is neither: a small, native, 100% offline desktop app that opens a PDF and gets out of your way — continuous smooth scrolling, real re-rasterized zoom instead of a blurry stretched image, and native printing on both Windows and Linux.

If you find it useful, drop a ⭐ on the repo — it really helps the project grow!

---

## 🚀 Download & Run

Grab the latest self-contained build for your OS from the [Releases](https://github.com/ram-ismael/VunLerDoc/releases) page — **no .NET runtime installation required**, everything needed to run is packed into the executable.

* **🪟 Windows (`win-x64`)**
  * Download and extract the Windows release asset.
  * Double-click `VunLerDoc.exe` to run it.

* **🐧 Linux (`linux-x64`)**
  * Download and extract the Linux release asset.
  * Grant execute permission (if needed) and run:
    ```bash
    chmod +x VunLerDoc
    ./VunLerDoc
    ```

On first launch, VunLerDoc registers itself in your OS's "Open with" menu for `.pdf` files — no admin rights needed on either platform.

---

## ✨ Features

| | |
|---|---|
| ⚡ **Continuous virtualized scrolling** | Only the pages actually on screen are rendered, so even huge documents open and scroll smoothly. |
| 🔍 **True re-rasterized zoom (25%–400%)** | Every zoom level is re-rendered from the PDF's vector content via PDFium — never a stretched bitmap — so text stays sharp at any scale. |
| 🖥️ **HiDPI-aware rendering** | Pages are rasterized at the display's real pixel density, not just its logical resolution. |
| 🧭 **Fit width / Fit page** | Instantly frame the document to the window. |
| 🔄 **Page rotation** | Rotate the current view in 90° increments. |
| 🖼️ **Thumbnail sidebar** | Background-generated page thumbnails for quick visual navigation, toggle on/off anytime. |
| 🖱️ **Drag & drop** | Drop a PDF straight onto the window to open it. |
| 🖨️ **Native printing** | GDI+ at 300 DPI on Windows; on Linux the original PDF is handed directly to CUPS (`lp`) for full-fidelity printing. |
| 🔌 **OS integration** | Registers as an "Open with" option for PDF files — Windows registry on Windows, a per-user `.desktop` entry on Linux. |
| 📦 **Self-contained builds** | Single-file, self-contained executables — nothing to install. |

---

## 🛠️ Tech Stack

* **Language**: C# / .NET 10
* **UI Framework**: [Avalonia UI](https://avaloniaui.net/) 12 (cross-platform XAML)
* **Architecture**: MVVM, via CommunityToolkit.Mvvm
* **PDF rendering**: [PDFium](https://pdfium.googlesource.com/pdfium/) (Google's PDF engine), via the PDFiumZ NuGet package
* **Imaging**: SkiaSharp

---

## 📁 Project Structure

```text
VunLerDoc/
├── Assets/               # App icon (logo.ico) and other visual resources
├── Converters/           # XAML value converters (bitmap, boolean, string)
├── Services/             # PDF rendering, printing and OS-integration services
│   ├── IPdfDocumentService.cs / PdfiumDocumentService.cs
│   ├── IPdfPrintService.cs / WindowsPdfPrintService.cs / LinuxPdfPrintService.cs
│   └── IFileAssociationService.cs / WindowsFileAssociationService.cs / LinuxFileAssociationService.cs
├── ViewModels/           # MainWindowViewModel, PdfPageItemViewModel
├── Views/                # MainWindow.axaml (UI)
├── Scripts/              # publish-windows.sh, publish-linux.sh
├── Program.cs
├── App.axaml
└── VunLerDoc.csproj
```

---

## 💻 Building from Source (Developers)

Requires the **.NET 10 SDK**.

1. Clone the repository:
   ```bash
   git clone https://github.com/ram-ismael/VunLerDoc.git
   cd VunLerDoc
   ```

2. Restore dependencies and build:
   ```bash
   dotnet restore
   dotnet build
   ```

3. Run the app:
   ```bash
   dotnet run --project VunLerDoc.csproj
   ```

4. To publish a new self-contained, single-file build:
   ```bash
   # Windows x64
   ./Scripts/publish-windows.sh

   # Linux x64
   ./Scripts/publish-linux.sh
   ```

---

## 🤝 Contributing

VunLerDoc is currently developed and maintained solo, but issues, suggestions and pull requests are very welcome:

1. Fork the project
2. Create a branch (`git checkout -b feature/my-feature`)
3. Commit your changes (`git commit -m 'feat: my new feature'`)
4. Push to the branch (`git push origin feature/my-feature`)
5. Open a Pull Request

---

## 🙏 Acknowledgements

* [PDFium](https://pdfium.googlesource.com/pdfium/) — the rendering engine at the core of VunLerDoc, via the [PDFiumZ](https://www.nuget.org/packages/PDFiumZ) NuGet package.
* [Avalonia UI](https://avaloniaui.net/) — the cross-platform UI framework this app is built on.

---

## 📄 License

This project is licensed under **MIT**. See the [LICENSE](LICENSE) file for details.

---

<p align="center">Designed and developed with ❤️ by <a href="https://github.com/ram-ismael">Ramadan Ismael</a>.</p>
