using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VunLerDoc.Services;

namespace VunLerDoc.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 0.10;
    private const int ItemSpacing = 16;

    /// <summary>Base DPI used for "100 %". 120 instead of 96 makes the default view ~25 % larger.</summary>
    private const double BaseDpi = 120.0;

    /// <summary>How many pages either side of the viewport to render ahead of time.</summary>
    private const int PrefetchRadius = 1;

    /// <summary>
    /// Max full-resolution bitmaps kept alive after scrolling out of view. Bounds memory while
    /// letting the user scroll back through recent pages without a re-render/blur flash.
    /// </summary>
    private const int RenderCacheCapacity = 20;

    private readonly IPdfDocumentService _pdfService;
    private readonly IPdfPrintService? _printService;

    public event EventHandler<TaskCompletionSource<PrintOptions?>>? PrintDialogRequested;

    /// <summary>
    /// Grace period between a page container being prepared and its full render actually being
    /// requested. During a fast mouse-wheel flick, VirtualizingStackPanel prepares and clears
    /// containers again within milliseconds as they pass through the realized range; queuing a
    /// full PDFium render for each of those would only add to the single-file render queue (see
    /// <see cref="PdfiumDocumentService"/>) and delay the page the user actually lands on. If a
    /// page is still prepared once this window elapses, it renders immediately as before.
    /// </summary>
    private const int PrepareDebounceMs = 70;

    private readonly HashSet<int> _preparedIndices = new();
    private readonly Dictionary<int, CancellationTokenSource> _renderTokens = new();
    private readonly Dictionary<int, CancellationTokenSource> _prepareDebounceTokens = new();

    // LRU cache of decoded full-resolution page bitmaps, keyed by page index. Cleared whenever
    // zoom/rotation changes since every entry is only valid for the DPI it was rendered at.
    private readonly Dictionary<int, Bitmap> _renderCache = new();
    private readonly LinkedList<int> _cacheUsageOrder = new();
    private readonly Dictionary<int, LinkedListNode<int>> _cacheNodes = new();

    private CancellationTokenSource? _thumbnailPassCts;
    private CancellationTokenSource? _zoomDebounceCts;

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
        PrintCommand?.NotifyCanExecuteChanged();
    }

    partial void OnZoomChanged(double value)
    {
        ZoomLabel = $"{value:P0}";
        foreach (var page in Pages)
            page.UpdateLayout(value, RotationDegrees, BaseDpi);

        // Every cached bitmap was rasterized at the old DPI - it's stale the instant zoom changes.
        InvalidateRenderCache();
        DebounceRerenderPreparedPages();
    }

    partial void OnRotationDegreesChanged(int value)
    {
        foreach (var page in Pages)
            page.UpdateLayout(Zoom, value, BaseDpi);

        InvalidateRenderCache();
        DebounceRerenderPreparedPages();
    }

    public MainWindowViewModel(IPdfDocumentService pdfService, IPdfPrintService? printService = null)
    {
        _pdfService = pdfService;
        _printService = printService;
        PrintCommand?.NotifyCanExecuteChanged();
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

            // Release native bitmap memory from the previous document before dropping references.
            InvalidateRenderCache();
            foreach (var oldPage in Pages)
                oldPage.Thumbnail?.Dispose();

            Pages.Clear();
            var count = _pdfService.PageCount;
            for (var i = 0; i < count; i++)
            {
                var item = new PdfPageItemViewModel(i);
                item.UpdateLayout(Zoom, RotationDegrees, BaseDpi);
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
            PrintCommand?.NotifyCanExecuteChanged();
        }
    }

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

                var effectiveDpi = Math.Max(result.PixelWidth, result.PixelHeight) >= thumbnailMaxDimension
                    ? probeDpi * (thumbnailMaxDimension / (float)Math.Max(result.PixelWidth, result.PixelHeight))
                    : probeDpi;

                page.SetNativeSize(result.PixelWidth, result.PixelHeight, effectiveDpi);
                page.UpdateLayout(Zoom, RotationDegrees, BaseDpi);

                // Decode on a background thread - PNG decode is real CPU work we don't want on the UI thread.
                var bitmap = await Task.Run(() => DecodeBitmap(result.Bytes), token);
                if (token.IsCancellationRequested) { bitmap.Dispose(); return; }
                page.Thumbnail = bitmap;
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

    public void NotifyPagePrepared(int index)
    {
        if (index < 0 || index >= Pages.Count) return;
        _preparedIndices.Add(index);
        RequestPageRenderDebounced(index);

        // Prefetch neighbours ahead of time so scrolling a page forward/back is usually an
        // instant cache hit instead of a visible low-res-then-sharp "blur" transition.
        for (var offset = 1; offset <= PrefetchRadius; offset++)
        {
            if (index + offset < Pages.Count) _ = RequestPageRenderAsync(index + offset, isPrefetch: true);
            if (index - offset >= 0) _ = RequestPageRenderAsync(index - offset, isPrefetch: true);
        }
    }

    /// <summary>
    /// Kicks off the full render for a newly-prepared page, waiting <see cref="PrepareDebounceMs"/>
    /// first unless the page is already cached (a cache hit is essentially free, so there is
    /// nothing to gain by delaying it). If the page is cleared again before the wait elapses -
    /// the "flew right past it" case during a fast flick - the render is skipped entirely instead
    /// of wastefully queuing behind the page(s) the user is actually settling on.
    /// </summary>
    private void RequestPageRenderDebounced(int index)
    {
        if (_renderCache.ContainsKey(index))
        {
            _ = RequestPageRenderAsync(index);
            return;
        }

        if (_prepareDebounceTokens.TryGetValue(index, out var stale))
        {
            stale.Cancel();
            stale.Dispose();
        }

        var cts = new CancellationTokenSource();
        _prepareDebounceTokens[index] = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PrepareDebounceMs, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            if (!_preparedIndices.Contains(index)) return; // cleared again before settling - skip

            await RequestPageRenderAsync(index);
        }, token);
    }

    public void NotifyPageCleared(int index)
    {
        if (index < 0 || index >= Pages.Count) return;
        _preparedIndices.Remove(index);

        if (_prepareDebounceTokens.TryGetValue(index, out var debounceCts))
        {
            debounceCts.Cancel();
            debounceCts.Dispose();
            _prepareDebounceTokens.Remove(index);
        }

        if (_renderTokens.TryGetValue(index, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _renderTokens.Remove(index);
        }

        // Deliberately NOT nulling the bitmap here. It stays alive in the LRU cache (see
        // CacheStore) and stays bound/visible on the page too - a routine virtualization
        // recycle (which fires constantly during smooth scrolling, for pages that are still
        // effectively on/near screen) must never downgrade an already-sharp page to the tiny
        // stretched thumbnail. The bitmap is only cleared from the page at the moment it is
        // actually disposed - see EvictExcess and CacheStore - so the UI never ends up holding
        // a dangling reference to a disposed bitmap.
    }

    /// <param name="isPrefetch">
    /// True for speculative look-ahead renders: skipped if a render is already in flight for
    /// this page, and never shows the busy indicator or a user-facing error.
    /// </param>
    private async Task RequestPageRenderAsync(int index, bool isPrefetch = false)
    {
        var page = Pages[index];

        if (TryGetCached(index, out var cachedBitmap))
        {
            page.FullImage = cachedBitmap;
            return;
        }

        if (_renderTokens.TryGetValue(index, out var existing))
        {
            if (isPrefetch) return; // Already rendering (real or prefetched) - don't duplicate the work.
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _renderTokens[index] = cts;
        var token = cts.Token;

        if (!isPrefetch) page.IsRendering = true;
        try
        {
            var dpi = (float)(BaseDpi * Zoom * Math.Max(1.0, RenderScaling));
            var bytes = await _pdfService.RenderPageAsync(index, dpi, RotationDegrees, token);
            if (token.IsCancellationRequested) return;

            // Decode on a background thread so a big page never stalls the UI thread mid-scroll.
            var bitmap = await Task.Run(() => DecodeBitmap(bytes), token);
            if (token.IsCancellationRequested) { bitmap.Dispose(); return; }

            CacheStore(index, bitmap);
            page.FullImage = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !isPrefetch)
                ErrorText = $"Não foi possível renderizar a página {page.PageNumber}: {ex.Message}";
        }
        finally
        {
            if (_renderTokens.TryGetValue(index, out var current) && current == cts)
            {
                _renderTokens.Remove(index);
                cts.Dispose();
            }
            if (!token.IsCancellationRequested) page.IsRendering = false;
        }
    }

    private static Bitmap DecodeBitmap(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        return new Bitmap(stream);
    }

    private bool TryGetCached(int index, out Bitmap bitmap)
    {
        if (_renderCache.TryGetValue(index, out var cached))
        {
            CacheTouch(index);
            bitmap = cached;
            return true;
        }
        bitmap = null!;
        return false;
    }

    private void CacheStore(int index, Bitmap bitmap)
    {
        if (_renderCache.TryGetValue(index, out var previous) && !ReferenceEquals(previous, bitmap))
        {
            previous.Dispose();
            // The caller (RequestPageRenderAsync) assigns the new bitmap to page.FullImage right
            // after this returns, but guard against a stale reference to the just-disposed one
            // in the meantime - see the comment on EvictExcess below for why this matters.
            if (index < Pages.Count && ReferenceEquals(Pages[index].FullImage, previous))
                Pages[index].FullImage = null;
        }

        _renderCache[index] = bitmap;
        CacheTouch(index);
        EvictExcess();
    }

    private void CacheTouch(int index)
    {
        if (_cacheNodes.TryGetValue(index, out var node))
            _cacheUsageOrder.Remove(node);
        _cacheNodes[index] = _cacheUsageOrder.AddFirst(index);
    }

    /// <summary>Evicts least-recently-used entries, but never a page currently on screen.</summary>
    private void EvictExcess()
    {
        var node = _cacheUsageOrder.Last;
        while (node is not null && _renderCache.Count > RenderCacheCapacity)
        {
            var previous = node.Previous;
            if (!_preparedIndices.Contains(node.Value))
            {
                if (_renderCache.Remove(node.Value, out var bitmap))
                {
                    bitmap.Dispose();

                    // A page stays showing its last good bitmap after being scrolled off-screen
                    // (see NotifyPageCleared) - now that the LRU cache has actually reclaimed it,
                    // drop the page's reference too so it falls back to the thumbnail instead of
                    // holding a disposed bitmap. Scrolling back re-renders it from scratch, same
                    // as before.
                    if (node.Value < Pages.Count && ReferenceEquals(Pages[node.Value].FullImage, bitmap))
                        Pages[node.Value].FullImage = null;
                }
                _cacheNodes.Remove(node.Value);
                _cacheUsageOrder.Remove(node);
            }
            node = previous;
        }
    }

    private void InvalidateRenderCache()
    {
        foreach (var bitmap in _renderCache.Values)
            bitmap.Dispose();
        _renderCache.Clear();
        _cacheNodes.Clear();
        _cacheUsageOrder.Clear();

        // Bitmaps just got disposed above - drop any dangling reference so the UI falls back
        // to the (separately-owned) thumbnail instead of drawing a disposed bitmap.
        foreach (var page in Pages)
            page.FullImage = null;
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

        foreach (var cts in _prepareDebounceTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _prepareDebounceTokens.Clear();

        _preparedIndices.Clear();
        _zoomDebounceCts?.Cancel();
    }

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

    public void SetZoomToFitWidth(double viewportWidth, double horizontalPadding = 64)
    {
        if (!HasDocument) return;
        var widest = Pages.Max(p => p.NativeWidthPoints);
        if (widest <= 0) return;
        var usable = Math.Max(100, viewportWidth - horizontalPadding);
        Zoom = Math.Clamp(usable / (widest / 72.0 * BaseDpi), MinZoom, MaxZoom);
    }

    public void SetZoomToFitPage(double viewportWidth, double viewportHeight, double padding = 64)
    {
        if (!HasDocument) return;
        var current = Pages[Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];
        var usableW = Math.Max(100, viewportWidth - padding);
        var usableH = Math.Max(100, viewportHeight - padding);
        var zoomW = usableW / (current.NativeWidthPoints / 72.0 * BaseDpi);
        var zoomH = usableH / (current.NativeHeightPoints / 72.0 * BaseDpi);
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

        var tcs = new TaskCompletionSource<PrintOptions?>();
        PrintDialogRequested?.Invoke(this, tcs);
        var options = await tcs.Task;
        if (options is null) return;

        try
        {
            IsBusy = true;
            StatusText = "A preparar impressão…";
            await _printService.PrintAsync(_pdfService, PageCount, DocumentName, options);
            StatusText = "Documento enviado para a impressora.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Impressão cancelada.";
        }
        catch (Exception ex)
        {
            ErrorText = $"Não foi possível imprimir: {ex.Message}";
            StatusText = "Falha na impressão.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateIndicators()
    {
        PageIndicator = PageCount == 0 ? "0 / 0" : $"{CurrentPage} / {PageCount}";
    }
}