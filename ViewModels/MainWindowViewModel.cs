using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
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

    /// <summary>
    /// Every PDF page is rasterized exactly once at this high-quality master resolution. Zoom
    /// only changes layout/scaling of the cached bitmap; it never asks PDFium to render again.
    /// 480 DPI is BaseDpi * MaxZoom, so a page remains 1:1 sharp up to 400% on a 1x display.
    /// </summary>
    private const float PreparedRenderDpi = 480f;

    /// <summary>How many pages either side of the viewport to render ahead of time.</summary>
    private const int PrefetchRadius = 1;

    /// <summary>
    /// Soft memory ceiling for decoded ARGB page bitmaps. Full-resolution PNG bytes are prepared
    /// progressively, while decoded display bitmaps use this bounded LRU cache.
    /// </summary>
    private const long RenderCacheMemoryBudgetBytes = 384L * 1024 * 1024; // ~384 MB

    /// <summary>
    /// Floor for decoded 480-DPI page bitmaps. The compressed master PNG for every prepared page
    /// remains available even after decoded bitmaps are evicted, so returning to a page never
    /// causes another PDFium render.
    /// </summary>
    private const int MinRenderCacheCapacity = 3;

    /// <summary>Keep the first two pages resident so opening/returning to the top never flashes.</summary>
    private static readonly HashSet<int> PinnedPageIndices = new() { 0, 1 };

    private int _renderCacheCapacity = 20;

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

    // LRU cache of decoded master page bitmaps, keyed by page index. Zoom and rotation never
    // invalidate this cache: they are presentation-only operations over the same master image.
    private readonly Dictionary<int, Bitmap> _renderCache = new();
    private readonly LinkedList<int> _cacheUsageOrder = new();
    private readonly Dictionary<int, LinkedListNode<int>> _cacheNodes = new();

    /// <summary>
    /// High-quality master PNG bytes prepared once per page, always in the PDF's original
    /// orientation. Visible pages can decode these immediately without another PDFium call;
    /// zoom/rotation only change presentation and never invalidate these bytes.
    /// </summary>
    private byte[]?[] _pageRasterBytes = Array.Empty<byte[]?>();

    // One shared raster task per page prevents the background primer and an on-screen request
    // from rasterizing the same page at the same time. The first request wins; everyone else
    // awaits that exact task. Only opening another document cancels this underlying work.
    private readonly object _pageRasterTaskLock = new();
    private Task<byte[]>?[] _pageRasterTasks = Array.Empty<Task<byte[]>?>();

    // Link metadata is independent from rasterization. Each page parses its annotations/text URLs
    // at most once per opened document and keeps the normalized hit rectangles in the page VM.
    // Zoom/rotation only transform the hit-test coordinates; they never re-read PDFium metadata.
    private readonly object _pageLinkTaskLock = new();
    private Task<IReadOnlyList<PdfLinkInfo>>?[] _pageLinkTasks = Array.Empty<Task<IReadOnlyList<PdfLinkInfo>>?>();

    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _primingCts;
    private CancellationTokenSource? _documentRenderCts;
    private int _documentGeneration;
    private int _renderRevision;

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
    [ObservableProperty] private bool _isOpeningDocument;
    [ObservableProperty] private bool _isSidebarVisible = true;
    [ObservableProperty] private string _statusText = "Abra um ficheiro PDF para começar.";
    [ObservableProperty] private string _backgroundStatusText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;

    /// <summary>True while pages beyond the initial visible page are being prepared in the background.</summary>
    [ObservableProperty] private bool _isPriming;

    /// <summary>Priming progress in the 0–1 range, for the loading overlay's progress bar.</summary>
    [ObservableProperty] private double _loadProgress;

    /// <summary>
    /// True once the first page has been rendered and the native reader surface can be revealed.
    /// Remaining pages continue to prepare at lower priority in the background.
    /// </summary>
    [ObservableProperty] private bool _isDocumentReady;

    public ObservableCollection<PdfPageItemViewModel> Pages { get; } = new();

    public bool HasDocument => PageCount > 0;

    /// <summary>
    /// True once the first full-quality page is ready. Background preparation never blocks
    /// navigation, zoom, rotation or printing.
    /// </summary>
    public bool IsInteractive => HasDocument && IsDocumentReady;
    public bool CanGoPrevious => IsInteractive && CurrentPage > 1;
    public bool CanGoNext => IsInteractive && CurrentPage < PageCount;
    public bool CanPrint => IsInteractive && _printService is not null;

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
        OnPropertyChanged(nameof(IsInteractive));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanPrint));
        PrintCommand?.NotifyCanExecuteChanged();
    }

    partial void OnIsDocumentReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractive));
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

        // Zoom is presentation-only. A page that has already been prepared keeps exactly the
        // same master bitmap/PNG; Avalonia scales it immediately with high-quality interpolation.
        // Crucially: do not cancel priming, clear caches or ask PDFium to render again here.
    }

    partial void OnRotationDegreesChanged(int value)
    {
        foreach (var page in Pages)
            page.UpdateLayout(Zoom, value, BaseDpi);

        // Rotation is also presentation-only. PdfPageItemViewModel exposes the angle to the view,
        // which rotates the already-cached bitmap without another PDFium call.
    }

    public MainWindowViewModel(IPdfDocumentService pdfService, IPdfPrintService? printService = null)
    {
        _pdfService = pdfService;
        _printService = printService;
        PrintCommand?.NotifyCanExecuteChanged();
    }

    public async Task OpenPdfAsync(string path)
    {
        var generation = ++_documentGeneration;

        ErrorText = string.Empty;
        IsBusy = false;
        IsPriming = false;
        IsDocumentReady = false;
        IsOpeningDocument = true;
        LoadProgress = 0;
        BackgroundStatusText = string.Empty;
        StatusText = "A abrir documento…";

        // Swap cancellation owners atomically. Background continuations can finish while a new
        // document is being opened, so reading the nullable fields twice (Cancel then Dispose)
        // is unsafe: the continuation may clear the field between those two reads.
        var openOwner = new CancellationTokenSource();
        var previousOpenOwner = Interlocked.Exchange(ref _openCts, openOwner);
        CancelAndDispose(previousOpenOwner);
        var openToken = openOwner.Token;

        // Stop work belonging to the previous document before installing the new render lifetime.
        // CancelBackgroundPriming also uses an atomic exchange for the same reason.
        CancelBackgroundPriming();
        CancelAllRenders();

        // The document render token deliberately lives beyond OpenPdfAsync: page rasterization
        // continues in the background and is cancelled only when a different PDF replaces it.
        var documentRenderOwner = new CancellationTokenSource();
        var previousDocumentRenderOwner = Interlocked.Exchange(ref _documentRenderCts, documentRenderOwner);
        CancelAndDispose(previousDocumentRenderOwner);

        // Reset presentation state before the new document is installed. Zoom/rotation no longer
        // invalidate raster data; the render revision below changes only when the document does.
        RotationDegrees = 0;
        Zoom = 1.0;
        _renderRevision++;

        // Detach UI-bound images before removing the old page models. Never Dispose a Bitmap
        // that has been published through an Avalonia Image.Source: layout/render may still hold
        // a short-lived platform reference while the binding/container is being recycled.
        // Once detached, the managed Bitmap can be reclaimed safely by GC after Avalonia releases
        // its last reference, avoiding Ref<IBitmapImpl> use-after-dispose crashes.
        InvalidateRenderCache();
        foreach (var oldPage in Pages)
            oldPage.Thumbnail = null;
        Pages.Clear();
        _pageRasterBytes = Array.Empty<byte[]?>();
        _pageRasterTasks = Array.Empty<Task<byte[]>?>();
        _pageLinkTasks = Array.Empty<Task<IReadOnlyList<PdfLinkInfo>>?>();
        PageCount = 0;
        CurrentPage = 0;
        DocumentPath = string.Empty;
        DocumentName = Path.GetFileName(path);
        Title = $"{DocumentName} — VunLerDoc";

        try
        {
            await _pdfService.OpenAsync(path, openToken);
            if (openToken.IsCancellationRequested || generation != _documentGeneration)
                return;

            DocumentPath = path;

            var count = _pdfService.PageCount;
            for (var i = 0; i < count; i++)
            {
                var item = new PdfPageItemViewModel(i);
                item.UpdateLayout(Zoom, RotationDegrees, BaseDpi);
                Pages.Add(item);
            }

            PageCount = count;
            CurrentPage = count > 0 ? 1 : 0;
            _pageRasterBytes = new byte[]?[count];
            _pageRasterTasks = new Task<byte[]>?[count];
            _pageLinkTasks = new Task<IReadOnlyList<PdfLinkInfo>>?[count];
            _renderCacheCapacity = 20;

            if (count == 0)
            {
                StatusText = "O documento não contém páginas.";
                IsDocumentReady = false;
                return;
            }

            // Only the first page is on the critical path. This keeps open-to-first-paint fast
            // while still guaranteeing the reader never reveals a blurry first page.
            StatusText = "A preparar a primeira página…";
            await RequestPageRenderAsync(0);
            if (openToken.IsCancellationRequested || generation != _documentGeneration)
                return;

            IsDocumentReady = true;
            IsOpeningDocument = false;

            // Link metadata is tiny compared with page pixels. Start page 1 immediately so its
            // index/URI links are interactive as soon as the native reader is revealed.
            _ = EnsurePageLinksAsync(0);

            // A newly opened document always starts on page 1. The ScrollViewer keeps its
            // numeric offset when its ItemsSource is replaced, so setting CurrentPage alone is
            // not sufficient: explicitly ask the view to bring the first page into view after
            // the first full-quality render is ready. Fit/zoom operations inside the same
            // document still preserve their current-page anchor as before.
            CurrentPage = 1;
            ScrollToPageRequested?.Invoke(this, 0);

            StatusText = $"{PageCount} {(PageCount == 1 ? "página" : "páginas")}";

            // Everything after page 1 is deliberately fire-and-continue UI work: the async loop
            // yields on every PDFium render and is queued at low priority inside the service, so
            // an on-screen page requested by scrolling always jumps ahead of it.
            StartBackgroundPriming();
        }
        catch (OperationCanceledException) when (openToken.IsCancellationRequested)
        {
            // A newer OpenPdfAsync call superseded this one.
        }
        catch (Exception ex)
        {
            if (generation != _documentGeneration)
                return;

            InvalidateRenderCache();
            foreach (var page in Pages)
                page.Thumbnail = null;
            Pages.Clear();
            PageCount = 0;
            CurrentPage = 0;
            DocumentPath = string.Empty;
            DocumentName = "Nenhum documento aberto";
            ErrorText = $"Não foi possível abrir o PDF: {ex.Message}";
            StatusText = "Falha ao abrir o documento.";
            Title = "VunLerDoc";
            IsPriming = false;
            IsDocumentReady = false;

            var failedDocumentRenderOwner = Interlocked.Exchange(ref _documentRenderCts, null);
            CancelAndDispose(failedDocumentRenderOwner);
        }
        finally
        {
            if (generation == _documentGeneration)
                IsOpeningDocument = false;

            // Only the invocation that still owns this CTS may detach/dispose it. If another
            // OpenPdfAsync already replaced it, that newer invocation disposed this owner while
            // swapping, so we must not touch the field or the newer CTS here.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _openCts, null, openOwner), openOwner))
                openOwner.Dispose();

            PrintCommand?.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Progressively rasterizes pages that are not currently needed on screen. Each page is
    /// rasterized exactly once at PreparedRenderDpi in its original orientation. These renders
    /// use the service's low-priority lane, while user-visible first-time requests use the
    /// high-priority lane. Zooming/rotation never restart this pass or invalidate its results.
    /// </summary>
    private async Task RunPrimingPassAsync(
        int documentGeneration,
        int renderRevision,
        CancellationTokenSource owner)
    {
        var token = owner.Token;
        var total = Pages.Count;

        try
        {
            for (var i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                if (documentGeneration != _documentGeneration || renderRevision != _renderRevision)
                    return;

                try
                {
                    var dpi = PreparedRenderDpi;
                    const int rotation = 0;
                    var bytes = _pageRasterBytes[i]
                        ?? await GetOrRenderMasterPageAsync(i, highPriority: false, token);

                    if (token.IsCancellationRequested || documentGeneration != _documentGeneration || renderRevision != _renderRevision)
                        return;

                    // GetOrRenderMasterPageAsync is shared by foreground and background callers,
                    // so this byte[] is the one-and-only PDFium raster for this page. We still
                    // finish any thumbnail/native-size decode that a cancelled viewport request
                    // may not have had time to apply.
                    var page = Pages[i];
                    var needsThumbnail = page.Thumbnail is null || !page.HasNativeSize;

                    if (needsThumbnail)
                    {
                        var (full, thumbnail, pixelWidth, pixelHeight) =
                            await Task.Run(() => DecodeFullAndThumbnail(bytes), token);

                        if (token.IsCancellationRequested || documentGeneration != _documentGeneration || renderRevision != _renderRevision)
                        {
                            full.Dispose();
                            thumbnail.Dispose();
                            return;
                        }

                        _pageRasterBytes[i] = bytes;
                        SetNativeSizeFromRendered(page, pixelWidth, pixelHeight, dpi, rotation);
                        if (i == 0) RecalculateRenderCacheCapacity();
                        page.UpdateLayout(Zoom, RotationDegrees, BaseDpi);

                        // This source is UI-bound; replace it without disposing the previous
                        // published bitmap during an active Avalonia layout/render frame.
                        page.Thumbnail = thumbnail;

                        page.FullImage = CacheStore(i, full);
                    }
                    else if (TryGetCached(i, out var cached))
                    {
                        page.FullImage = cached;
                    }
                    else
                    {
                        var full = await Task.Run(() => DecodeBitmap(bytes), token);
                        if (token.IsCancellationRequested || documentGeneration != _documentGeneration || renderRevision != _renderRevision)
                        {
                            full.Dispose();
                            return;
                        }

                        page.FullImage = CacheStore(i, full);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested && documentGeneration == _documentGeneration)
                        ErrorText = $"Não foi possível preparar a página {i + 1}: {ex.Message}";
                }

                LoadProgress = (i + 1) / (double)total;
                BackgroundStatusText = $"A preparar em segundo plano · {i + 1} / {total}";
            }
        }
        finally
        {
            // Atomically detach only if this pass still owns the field. A concurrent cancel may
            // already have exchanged the field to null and disposed this owner.
            if (ReferenceEquals(Interlocked.CompareExchange(ref _primingCts, null, owner), owner))
            {
                if (!token.IsCancellationRequested && documentGeneration == _documentGeneration && renderRevision == _renderRevision)
                {
                    LoadProgress = 1;
                    BackgroundStatusText = string.Empty;
                    StatusText = $"{PageCount} {(PageCount == 1 ? "página" : "páginas")}";
                }

                IsPriming = false;
                owner.Dispose();
            }
        }
    }

    private void StartBackgroundPriming()
    {
        CancelBackgroundPriming();

        if (!IsDocumentReady || PageCount <= 1 || _pageRasterBytes.Length != PageCount)
        {
            LoadProgress = PageCount == 1 ? 1 : 0;
            BackgroundStatusText = string.Empty;
            return;
        }

        var owner = new CancellationTokenSource();
        var replacedOwner = Interlocked.Exchange(ref _primingCts, owner);
        CancelAndDispose(replacedOwner);
        IsPriming = true;
        var readyCount = _pageRasterBytes.Count(bytes => bytes is not null);
        LoadProgress = Math.Clamp(readyCount / (double)PageCount, 0, 1);
        BackgroundStatusText = $"A preparar em segundo plano · {readyCount} / {PageCount}";

        _ = RunPrimingPassAsync(_documentGeneration, _renderRevision, owner);
    }

    private void CancelBackgroundPriming()
    {
        // Detach first, then cancel/dispose the captured owner. RunPrimingPassAsync can finish at
        // exactly the same time; exchanging the field prevents it from turning null between a
        // field-level Cancel() and Dispose() call (the crash previously seen here).
        var owner = Interlocked.Exchange(ref _primingCts, null);
        CancelAndDispose(owner);

        IsPriming = false;
        BackgroundStatusText = string.Empty;
    }

    private static void CancelAndDispose(CancellationTokenSource? owner)
    {
        if (owner is null)
            return;

        try
        {
            owner.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Another completed continuation may already have disposed its own captured owner.
        }
        finally
        {
            owner.Dispose();
        }
    }

    public void NotifyPagePrepared(int index)
    {
        if (index < 0 || index >= Pages.Count) return;
        _preparedIndices.Add(index);
        RequestPageRenderDebounced(index);
        _ = EnsurePageLinksAsync(index);

        // Prefetch neighbours ahead of time so scrolling a page forward/back is usually an
        // instant cache hit instead of a visible low-res-then-sharp "blur" transition.
        for (var offset = 1; offset <= PrefetchRadius; offset++)
        {
            if (index + offset < Pages.Count) _ = RequestPageRenderAsync(index + offset, isPrefetch: true);
            if (index - offset >= 0) _ = RequestPageRenderAsync(index - offset, isPrefetch: true);
        }
    }

    /// <summary>
    /// Parses annotations/destinations and automatically detected web URLs for one page. The
    /// result is shared by viewport/preload/click callers and is retained for the lifetime of
    /// the currently opened document. This does not render page pixels.
    /// </summary>
    public async Task EnsurePageLinksAsync(int index)
    {
        if (index < 0 || index >= Pages.Count)
            return;

        var page = Pages[index];
        if (page.LinksLoaded)
            return;

        var generation = _documentGeneration;
        var documentOwner = Volatile.Read(ref _documentRenderCts);
        if (documentOwner is null)
            return;

        CancellationToken token;
        try
        {
            token = documentOwner.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Task<IReadOnlyList<PdfLinkInfo>> sharedTask;
        lock (_pageLinkTaskLock)
        {
            if (generation != _documentGeneration || index >= _pageLinkTasks.Length)
                return;

            sharedTask = _pageLinkTasks[index]
                         ??= _pdfService.GetPageLinksAsync(index, token);
        }

        try
        {
            var links = await sharedTask;
            if (token.IsCancellationRequested || generation != _documentGeneration ||
                index < 0 || index >= Pages.Count)
            {
                return;
            }

            Pages[index].SetLinks(links);
        }
        catch (OperationCanceledException)
        {
            // Opening another document invalidates this page's metadata.
        }
        catch
        {
            // Link parsing must never make a valid PDF unreadable. Mark the page as parsed with
            // no links; the raster/native-reader path stays fully functional.
            if (generation == _documentGeneration && index >= 0 && index < Pages.Count)
                Pages[index].SetLinks(Array.Empty<PdfLinkInfo>());
        }
    }

    /// <summary>
    /// Kicks off the full render for a newly-prepared page, waiting <see cref="PrepareDebounceMs"/>
    /// first unless the page is already decoded OR already primed (either case is essentially
    /// free/instant, so there is nothing to gain by delaying it). If the page is cleared again
    /// before the wait elapses - the "flew right past it" case during a fast flick - the render
    /// is skipped entirely instead of wastefully queuing a real PDFium call behind the page(s)
    /// the user is actually settling on.
    /// </summary>
    private void RequestPageRenderDebounced(int index)
    {
        var hasDecodedBitmap = _renderCache.ContainsKey(index);
        var hasPrimedBytes = index < _pageRasterBytes.Length && _pageRasterBytes[index] is not null;

        if (hasDecodedBitmap || hasPrimedBytes)
        {
            // A cache hit only needs an in-memory decode, so show it immediately.
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
        _ = RequestPageRenderAfterDelayAsync(index, cts);
    }

    private async Task RequestPageRenderAfterDelayAsync(int index, CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            await Task.Delay(PrepareDebounceMs, token);
            if (token.IsCancellationRequested || !_preparedIndices.Contains(index))
                return;

            await RequestPageRenderAsync(index);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_prepareDebounceTokens.TryGetValue(index, out var current) && ReferenceEquals(current, owner))
            {
                _prepareDebounceTokens.Remove(index);
                owner.Dispose();
            }
        }
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

        // Deliberately NOT nulling the bitmap here. Routine virtualization fires constantly
        // during smooth scrolling and must not downgrade an already-sharp page just because its
        // container is being recycled. EvictExcess may detach a genuinely old page later, but it
        // never manually disposes an image that has already been published to Avalonia.
    }

    /// <param name="isPrefetch">
    /// True for speculative look-ahead renders: skipped if a render is already in flight for
    /// this page, and never shows the busy indicator or a user-facing error.
    /// </param>
    private async Task RequestPageRenderAsync(int index, bool isPrefetch = false)
    {
        if (index < 0 || index >= Pages.Count)
            return;

        var page = Pages[index];
        var revision = _renderRevision;
        const int rotation = 0;
        var dpi = PreparedRenderDpi;

        if (TryGetCached(index, out var cachedBitmap))
        {
            page.FullImage = cachedBitmap;
            return;
        }

        if (_renderTokens.TryGetValue(index, out var existing))
        {
            if (isPrefetch)
                return;

            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _renderTokens[index] = cts;
        var token = cts.Token;

        if (!isPrefetch)
            page.IsRendering = true;

        try
        {
            var primed = index < _pageRasterBytes.Length ? _pageRasterBytes[index] : null;
            var bytes = primed ?? await GetOrRenderMasterPageAsync(index, highPriority: true, token);

            if (token.IsCancellationRequested || revision != _renderRevision || index >= Pages.Count)
                return;

            var needsThumbnail = page.Thumbnail is null || !page.HasNativeSize;
            if (needsThumbnail)
            {
                var (full, thumbnail, pixelWidth, pixelHeight) =
                    await Task.Run(() => DecodeFullAndThumbnail(bytes), token);

                if (token.IsCancellationRequested || revision != _renderRevision || index >= Pages.Count)
                {
                    full.Dispose();
                    thumbnail.Dispose();
                    return;
                }

                SetNativeSizeFromRendered(page, pixelWidth, pixelHeight, dpi, rotation);
                if (index == 0) RecalculateRenderCacheCapacity();
                page.UpdateLayout(Zoom, RotationDegrees, BaseDpi);

                // Never manually Dispose an image that has been bound to Avalonia Image.Source.
                // Binding replacement is synchronous at the property level, but the platform
                // renderer can still hold the previous bitmap through the current layout frame.
                page.Thumbnail = thumbnail;

                page.FullImage = CacheStore(index, full);
            }
            else
            {
                var bitmap = await Task.Run(() => DecodeBitmap(bytes), token);
                if (token.IsCancellationRequested || revision != _renderRevision || index >= Pages.Count)
                {
                    bitmap.Dispose();
                    return;
                }

                page.FullImage = CacheStore(index, bitmap);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && revision == _renderRevision && !isPrefetch)
                ErrorText = $"Não foi possível renderizar a página {page.PageNumber}: {ex.Message}";
        }
        finally
        {
            if (_renderTokens.TryGetValue(index, out var current) && ReferenceEquals(current, cts))
            {
                _renderTokens.Remove(index);
                page.IsRendering = false;
                cts.Dispose();
            }
        }
    }

    private Task<byte[]> GetOrRenderMasterPageAsync(
        int index,
        bool highPriority,
        CancellationToken waitCancellationToken)
    {
        if (index < 0 || index >= _pageRasterBytes.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        Task<byte[]> sharedTask;
        lock (_pageRasterTaskLock)
        {
            if (_pageRasterBytes[index] is { } ready)
                return Task.FromResult(ready);

            if (_pageRasterTasks[index] is { } existing)
                return existing.WaitAsync(waitCancellationToken);

            // Read the owner once. A new OpenPdfAsync can atomically replace/cancel/dispose the
            // field while an old viewport/background request is arriving here.
            var documentOwner = Volatile.Read(ref _documentRenderCts)
                ?? throw new InvalidOperationException("No PDF document render lifetime is active.");

            CancellationToken documentToken;
            try
            {
                documentToken = documentOwner.Token;
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException("The PDF document render lifetime has ended.");
            }

            var generation = _documentGeneration;
            if (documentToken.IsCancellationRequested ||
                !ReferenceEquals(documentOwner, Volatile.Read(ref _documentRenderCts)))
                throw new OperationCanceledException("The PDF document changed while the page render was being queued.");

            sharedTask = RenderMasterPageOnceAsync(index, highPriority, generation, documentToken);
            _pageRasterTasks[index] = sharedTask;

            // A genuine render failure may be retried later. Cancellation caused by opening a new
            // document is harmless because that operation replaces the arrays entirely.
            _ = sharedTask.ContinueWith(
                completed =>
                {
                    if (completed.IsCompletedSuccessfully) return;
                    lock (_pageRasterTaskLock)
                    {
                        if (index < _pageRasterTasks.Length &&
                            ReferenceEquals(_pageRasterTasks[index], sharedTask))
                            _pageRasterTasks[index] = null;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return sharedTask.WaitAsync(waitCancellationToken);
    }

    private async Task<byte[]> RenderMasterPageOnceAsync(
        int index,
        bool highPriority,
        int documentGeneration,
        CancellationToken documentToken)
    {
        var bytes = highPriority
            ? await _pdfService.RenderPageAsync(index, PreparedRenderDpi, 0, documentToken)
            : await _pdfService.RenderPageBackgroundAsync(index, PreparedRenderDpi, 0, documentToken);

        documentToken.ThrowIfCancellationRequested();
        if (documentGeneration != _documentGeneration)
            throw new OperationCanceledException("The PDF document changed while the page was rendering.");

        lock (_pageRasterTaskLock)
        {
            if (documentGeneration != _documentGeneration || index >= _pageRasterBytes.Length)
                throw new OperationCanceledException("The PDF document changed while the page was rendering.");

            _pageRasterBytes[index] ??= bytes;
            return _pageRasterBytes[index]!;
        }
    }

    private void RecalculateRenderCacheCapacity()
    {
        if (Pages.Count == 0 || !Pages[0].HasNativeSize)
            return;

        var dpi = PreparedRenderDpi;
        var pixelWidth = Math.Max(1.0, Pages[0].NativeWidthPoints / 72.0 * dpi);
        var pixelHeight = Math.Max(1.0, Pages[0].NativeHeightPoints / 72.0 * dpi);
        var bytesPerPage = Math.Max(1L, (long)Math.Ceiling(pixelWidth * pixelHeight * 4.0));

        _renderCacheCapacity = (int)Math.Clamp(
            RenderCacheMemoryBudgetBytes / bytesPerPage,
            MinRenderCacheCapacity,
            Math.Max(MinRenderCacheCapacity, Pages.Count));
    }

    private static void SetNativeSizeFromRendered(
        PdfPageItemViewModel page,
        int renderedPixelWidth,
        int renderedPixelHeight,
        float dpi,
        int rotationDegrees)
    {
        var normalizedRotation = ((rotationDegrees % 360) + 360) % 360;
        if (normalizedRotation is 90 or 270)
            (renderedPixelWidth, renderedPixelHeight) = (renderedPixelHeight, renderedPixelWidth);

        page.SetNativeSize(renderedPixelWidth, renderedPixelHeight, dpi);
    }

    private static Bitmap DecodeBitmap(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes, writable: false);
        return new Bitmap(stream);
    }

    /// <summary>
    /// Decodes a primed page render into both the full-resolution display bitmap and a small
    /// sidebar thumbnail, from a single PDFium output. The thumbnail is a cheap in-process
    /// downscale of pixels PDFium already produced - no second native render, unlike the old
    /// separate thumbnail pass.
    /// </summary>
    private static (Bitmap Full, Bitmap Thumbnail, int PixelWidth, int PixelHeight) DecodeFullAndThumbnail(byte[] pngBytes)
    {
        const int thumbnailMaxDimension = 220;

        using var decoded = SKBitmap.Decode(pngBytes)
            ?? throw new InvalidOperationException("Não foi possível descodificar a página renderizada.");

        var full = new Bitmap(new MemoryStream(pngBytes, writable: false));

        var longestSide = Math.Max(decoded.Width, decoded.Height);
        var thumbnail = longestSide <= thumbnailMaxDimension
            ? new Bitmap(new MemoryStream(pngBytes, writable: false))
            : new Bitmap(new MemoryStream(ResizePng(decoded, thumbnailMaxDimension), writable: false));

        return (full, thumbnail, decoded.Width, decoded.Height);
    }

    /// <summary>Downscales an already-decoded page to fit within <paramref name="maxDimension"/>, re-encoded as PNG.</summary>
    private static byte[] ResizePng(SKBitmap source, int maxDimension)
    {
        var scale = maxDimension / (float)Math.Max(source.Width, source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 85);
        return data.ToArray();
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

    /// <summary>
    /// Stores a decoded bitmap and returns the canonical instance for this page. If foreground
    /// and background decoding finish at nearly the same time, the first bitmap wins and the
    /// duplicate (which has never been published to Image.Source) can be disposed safely.
    /// A bitmap already published to the UI is never disposed here.
    /// </summary>
    private Bitmap CacheStore(int index, Bitmap bitmap)
    {
        if (_renderCache.TryGetValue(index, out var existing))
        {
            CacheTouch(index);
            if (!ReferenceEquals(existing, bitmap))
                bitmap.Dispose(); // Safe: this duplicate was never assigned to a page property.
            return existing;
        }

        _renderCache[index] = bitmap;
        CacheTouch(index);
        EvictExcess();
        return bitmap;
    }

    private void CacheTouch(int index)
    {
        if (_cacheNodes.TryGetValue(index, out var node))
            _cacheUsageOrder.Remove(node);
        _cacheNodes[index] = _cacheUsageOrder.AddFirst(index);
    }

    /// <summary>
    /// Evicts least-recently-used decoded entries down to <see cref="_renderCacheCapacity"/>, but
    /// never a page currently on screen. The master PNG bytes remain in
    /// <see cref="_pageRasterBytes"/>, so revisiting an evicted page only performs an in-memory
    /// decode and never another PDFium render.
    ///
    /// IMPORTANT: do not Dispose an evicted Bitmap here. It may have been published to an
    /// Avalonia Image.Source and a recycled container can still hold the platform bitmap for the
    /// remainder of the current layout/render frame. Removing all managed references lets GC
    /// reclaim it only after Avalonia has truly released it, preventing ObjectDisposedException.
    /// </summary>
    private void EvictExcess()
    {
        var node = _cacheUsageOrder.Last;
        while (node is not null && _renderCache.Count > _renderCacheCapacity)
        {
            var previous = node.Previous;
            if (!_preparedIndices.Contains(node.Value) && !PinnedPageIndices.Contains(node.Value))
            {
                if (_renderCache.Remove(node.Value, out var bitmap) &&
                    node.Value < Pages.Count &&
                    ReferenceEquals(Pages[node.Value].FullImage, bitmap))
                {
                    // Detach first. Do not manually dispose a UI-published Bitmap.
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
        // Detach UI properties before dropping cache references. Published Avalonia Bitmaps are
        // intentionally left to GC/ref-counting rather than manually disposed while controls may
        // still be measuring or rendering them.
        foreach (var page in Pages)
            page.FullImage = null;

        _renderCache.Clear();
        _cacheNodes.Clear();
        _cacheUsageOrder.Clear();
    }

    private void CancelActivePageRenders()
    {
        foreach (var (index, cts) in _renderTokens.ToArray())
        {
            cts.Cancel();
            cts.Dispose();
            if (index >= 0 && index < Pages.Count)
                Pages[index].IsRendering = false;
        }
        _renderTokens.Clear();

        foreach (var cts in _prepareDebounceTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _prepareDebounceTokens.Clear();
    }

    private void CancelAllRenders()
    {
        CancelActivePageRenders();
        _preparedIndices.Clear();
    }

    public void UpdateCurrentPageFromScrollOffset(
        double verticalOffset,
        double viewportHeight,
        double topPadding = 0)
    {
        if (Pages.Count == 0) return;

        // Track the page occupying the centre of the viewport rather than the first page whose
        // slot touches the top edge. This mirrors what the user perceives as the active page and
        // makes the thumbnail rail follow scrolling without jumping early between two pages.
        var viewportAnchor = verticalOffset + Math.Max(0, viewportHeight) / 2.0;
        double accumulated = Math.Max(0, topPadding);

        foreach (var page in Pages)
        {
            var slot = page.DisplayHeight + ItemSpacing;
            if (viewportAnchor < accumulated + slot)
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