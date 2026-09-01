using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VunLerDoc.Services;

namespace VunLerDoc.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.10;
    private const int ItemSpacing = 16; // must match the spacing used by the page list panel in XAML

    private readonly IPdfDocumentService _pdfService;
    private readonly IPdfPrintService? _printService;

    // Pages currently realized by the virtualizing list (kept in sync by the view via
    // NotifyPagePrepared/NotifyPageCleared). Used to know which pages to re-render after a
    // zoom change settles, without touching pages that are scrolled far away.
    private readonly HashSet<int> _preparedIndices = new();
    private readonly Dictionary<int, CancellationTokenSource> _renderTokens = new();

    private CancellationTokenSource? _thumbnailPassCts;
    private CancellationTokenSource? _zoomDebounceCts;

    /// <summary>Raised when the view should scroll the page list to bring an index into view.</summary>
    public event EventHandler<int>? ScrollToPageRequested;

    [ObservableProperty] private string _title = "VunLerDoc";
    [ObservableProperty] private string _documentName = "Nenhum documento aberto";
    [ObservableProperty] private string _documentPath = string.Empty;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private int _rotationDegrees;
    [ObservableProperty] private string _pageIndicator = "0 / 0";
    [ObservableProperty] private string _zoomLabel = "100%";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSidebarVisible = true;
    [ObservableProperty] private string _statusText = "Abra um ficheiro PDF para começar.";
    [ObservableProperty] private string _errorText = string.Empty;

    /// <summary>Render resolution multiplier for the current display (set by the view from
    /// TopLevel.RenderScaling) so full-resolution pages are rasterized at physical-pixel
    /// density, not just DIP density — this is what keeps text sharp on HiDPI screens.</summary>
    public double RenderScaling { get; set; } = 1.0;

    public ObservableCollection<PdfPageItemViewModel> Pages { get; } = new();

    public bool HasDocument => PageCount > 0;
    public bool CanGoPrevious => HasDocument && CurrentPage > 1;
    public bool CanGoNext => HasDocument && CurrentPage < PageCount;
    public bool CanPrint => HasDocument && _printService is not null;

    partial void OnCurrentPageChanged(int value)
    {
        UpdateIndicators();
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnPageCountChanged(int value)
    {
        UpdateIndicators();
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanPrint));
    }

    partial void OnZoomChanged(double value)
    {
        ZoomLabel = $"{value:P0}";
        foreach (var page in Pages)
            page.UpdateLayout(value, RotationDegrees);

        DebounceRerenderPreparedPages();
    }

    partial void OnRotationDegreesChanged(int value)
    {
        foreach (var page in Pages)
            page.UpdateLayout(Zoom, value);
        DebounceRerenderPreparedPages();
    }

    public MainWindowViewModel(IPdfDocumentService pdfService, IPdfPrintService? printService = null)
    {
        _pdfService = pdfService;
        _printService = printService;
    }

    public async Task OpenPdfAsync(string path)
    {
        ErrorText = string.Empty;
        IsBusy = true;
        StatusText = "A abrir documento…";
        CancelAllRenders();
        _thumbnailPassCts?.Cancel();

        try
        {
            await _pdfService.OpenAsync(path);
            DocumentPath = path;
            DocumentName = Path.GetFileName(path);
            RotationDegrees = 0;
            Zoom = 1.0;

            Pages.Clear();
            var count = _pdfService.PageCount;
            for (var i = 0; i < count; i++)
            {
                var item = new PdfPageItemViewModel(i);
                item.UpdateLayout(Zoom, RotationDegrees);
                Pages.Add(item);
            }

            PageCount = count;
            CurrentPage = count > 0 ? 1 : 0;
            StatusText = $"{PageCount} {(PageCount == 1 ? "página" : "páginas")}";
            Title = $"{DocumentName} — VunLerDoc";

            _thumbnailPassCts = new CancellationTokenSource();
            _ = RunThumbnailPassAsync(_thumbnailPassCts.Token);
        }
        catch (Exception ex)
        {
            Pages.Clear();
            PageCount = 0;
            DocumentPath = string.Empty;
            DocumentName = "Nenhum documento aberto";
            ErrorText = $"Não foi possível abrir o PDF: {ex.Message}";
            StatusText = "Falha ao abrir o documento.";
            Title = "VunLerDoc";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Generates thumbnails/native sizes for every page, in order, in the background.
    /// Runs sequentially — the pdfium service itself serializes access, so extra concurrency
    /// here wouldn't speed things up, only add overhead.</summary>
    private async Task RunThumbnailPassAsync(CancellationToken token)
    {
        const int thumbnailMaxDimension = 220;
        const float probeDpi = 36f;

        foreach (var page in Pages)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var result = await _pdfService.RenderThumbnailAsync(page.Index, thumbnailMaxDimension, token);
                if (token.IsCancellationRequested) return;

                // The service re-renders at an adjusted dpi internally when downscaling is
                // needed; either way the returned pixel size and the dpi it was produced at
                // are consistent, so back-compute the effective dpi from what we know we asked
                // for versus what we probed — simplest correct anchor is the probe dpi when no
                // second pass was needed, otherwise infer from pixel size directly.
                var effectiveDpi = Math.Max(result.PixelWidth, result.PixelHeight) >= thumbnailMaxDimension
                    ? probeDpi * (thumbnailMaxDimension / (float)Math.Max(result.PixelWidth, result.PixelHeight))
                    : probeDpi;

                page.SetNativeSize(result.PixelWidth, result.PixelHeight, effectiveDpi);
                page.UpdateLayout(Zoom, RotationDegrees);
                page.Thumbnail = result.Bytes;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A single bad page shouldn't stop the rest of the document from loading.
            }
        }
    }

    /// <summary>Called by the view when a page's container is realized (on/near screen).</summary>
    public void NotifyPagePrepared(int index)
    {
        if (index < 0 || index >= Pages.Count) return;
        _preparedIndices.Add(index);
        _ = RequestPageRenderAsync(index);
    }

    /// <summary>Called by the view when a page's container is recycled (scrolled far away).</summary>
    public void NotifyPageCleared(int index)
    {
        if (index < 0 || index >= Pages.Count) return;
        _preparedIndices.Remove(index);

        if (_renderTokens.TryGetValue(index, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _renderTokens.Remove(index);
        }

        // Drop the full-resolution bitmap to keep memory bounded; the cheap thumbnail stays.
        Pages[index].FullImage = null;
    }

    private async Task RequestPageRenderAsync(int index)
    {
        var page = Pages[index];

        if (_renderTokens.TryGetValue(index, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _renderTokens[index] = cts;
        var token = cts.Token;

        page.IsRendering = true;
        try
        {
            var dpi = (float)(96.0 * Zoom * Math.Max(1.0, RenderScaling));
            var bytes = await _pdfService.RenderPageAsync(index, dpi, RotationDegrees, token);
            if (token.IsCancellationRequested) return;
            page.FullImage = bytes;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                ErrorText = $"Não foi possível renderizar a página {page.PageNumber}: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                page.IsRendering = false;
        }
    }

    private void DebounceRerenderPreparedPages()
    {
        _zoomDebounceCts?.Cancel();
        _zoomDebounceCts?.Dispose();
        _zoomDebounceCts = new CancellationTokenSource();
        var token = _zoomDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            foreach (var index in _preparedIndices.ToArray())
            {
                if (token.IsCancellationRequested) return;
                await RequestPageRenderAsync(index);
            }
        }, token);
    }

    private void CancelAllRenders()
    {
        foreach (var cts in _renderTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _renderTokens.Clear();
        _preparedIndices.Clear();
        _zoomDebounceCts?.Cancel();
    }

    /// <summary>Updates CurrentPage from the page list's scroll offset, using each page's
    /// exact known layout height — accurate without needing any extra virtualization APIs.</summary>
    public void UpdateCurrentPageFromScrollOffset(double verticalOffset)
    {
        if (Pages.Count == 0) return;

        double accumulated = 0;
        foreach (var page in Pages)
        {
            var slot = page.DisplayHeight + ItemSpacing;
            if (accumulated + slot > verticalOffset + 1)
            {
                if (CurrentPage != page.PageNumber)
                    CurrentPage = page.PageNumber;
                return;
            }
            accumulated += slot;
        }

        if (CurrentPage != Pages.Count)
            CurrentPage = Pages.Count;
    }

    [RelayCommand]
    private void NextPage()
    {
        if (!CanGoNext) return;
        CurrentPage++;
        ScrollToPageRequested?.Invoke(this, CurrentPage - 1);
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (!CanGoPrevious) return;
        CurrentPage--;
        ScrollToPageRequested?.Invoke(this, CurrentPage - 1);
    }

    public void GoToPage(int pageNumber)
    {
        if (!HasDocument) return;
        pageNumber = Math.Clamp(pageNumber, 1, PageCount);
        CurrentPage = pageNumber;
        ScrollToPageRequested?.Invoke(this, pageNumber - 1);
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round(Zoom + ZoomStep, 2));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round(Zoom - ZoomStep, 2));

    [RelayCommand]
    private void ResetZoom() => Zoom = 1.0;

    /// <summary>Fits the widest page to the given viewport width (called by the view, which
    /// owns the actual pixel measurements of the scroll viewport).</summary>
    public void SetZoomToFitWidth(double viewportWidth, double horizontalPadding = 64)
    {
        if (!HasDocument) return;
        var widest = Pages.Max(p => p.NativeWidthPoints);
        if (widest <= 0) return;
        var usable = Math.Max(100, viewportWidth - horizontalPadding);
        Zoom = Math.Clamp(usable / (widest / 72.0 * 96.0), MinZoom, MaxZoom);
    }

    public void SetZoomToFitPage(double viewportWidth, double viewportHeight, double padding = 64)
    {
        if (!HasDocument) return;
        var current = Pages[Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];
        var usableW = Math.Max(100, viewportWidth - padding);
        var usableH = Math.Max(100, viewportHeight - padding);
        var zoomW = usableW / (current.NativeWidthPoints / 72.0 * 96.0);
        var zoomH = usableH / (current.NativeHeightPoints / 72.0 * 96.0);
        Zoom = Math.Clamp(Math.Min(zoomW, zoomH), MinZoom, MaxZoom);
    }

    [RelayCommand]
    private void RotateClockwise() => RotationDegrees = (RotationDegrees + 90) % 360;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task PrintAsync()
    {
        if (_printService is null || !HasDocument) return;
        try
        {
            StatusText = "A preparar impressão…";
            await _printService.PrintAsync(_pdfService, PageCount, DocumentName);
            StatusText = $"{PageCount} {(PageCount == 1 ? "página" : "páginas")}";
        }
        catch (Exception ex)
        {
            ErrorText = $"Não foi possível imprimir: {ex.Message}";
        }
    }

    private void UpdateIndicators()
    {
        PageIndicator = PageCount == 0 ? "0 / 0" : $"{CurrentPage} / {PageCount}";
    }
}
