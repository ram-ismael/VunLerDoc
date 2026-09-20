using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VunLerDoc.Services;
using VunLerDoc.ViewModels;

namespace VunLerDoc.Views;

public partial class MainWindow : Window
{
    // These values mirror the PDF list template in MainWindow.axaml. They let us preserve the
    // exact page/position at the viewport centre while a fit-to-width/page operation changes
    // every page's layout size.
    private const double PageListTopPadding = 28.0;
    private const double PageItemSpacing = 16.0;

    private MainWindowViewModel? _boundViewModel;
    private ViewportAnchor? _pendingViewportAnchor;
    private int _lastSidebarIndex = -1;
    private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);

    private readonly record struct ViewportAnchor(int PageIndex, double RelativePosition);

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        // Capture Ctrl+wheel at the Window during tunnelling. The Window is the first element
        // on the route, so marking the event handled here prevents ListBox/ScrollViewer from
        // seeing the same wheel gesture and scrolling the PDF. Plain wheel is left untouched.
        AddHandler(
            InputElement.PointerWheelChangedEvent,
            PageScrollViewerPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        // Tunnel (not bubble) so this runs *before* PagesList - a focused ListBox has its own
        // built-in Up/Down = "move selection" behaviour, which would otherwise intercept the
        // arrow keys before our scroll/page-navigation handling ever saw them.
        AddHandler(KeyDownEvent, OnArrowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.ScrollToPageRequested -= OnScrollToPageRequested;
            _boundViewModel.PrintDialogRequested -= OnPrintDialogRequested;
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundViewModel = DataContext as MainWindowViewModel;
        _lastSidebarIndex = -1;

        if (_boundViewModel is null)
            return;

        _boundViewModel.ScrollToPageRequested += OnScrollToPageRequested;
        _boundViewModel.PrintDialogRequested += OnPrintDialogRequested;
        _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncSidebarToCurrentPage(_boundViewModel, forceScroll: true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel vm)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsOpeningDocument) && vm.IsOpeningDocument)
        {
            // Never carry a fit-to-width/page anchor from the previous PDF into the new one.
            // The new document will explicitly request page 1 once its first page is ready.
            _pendingViewportAnchor = null;
            _lastSidebarIndex = -1;
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsDocumentReady) && vm.IsDocumentReady)
        {
            // A new ItemsSource can inherit the ScrollViewer's previous numeric offset. Force
            // the newly-ready document to the absolute top so opening a PDF always begins at
            // the start of page 1, even if page 1 would already count as partially visible.
            _pendingViewportAnchor = null;
            PageScrollViewer.Offset = new Vector(PageScrollViewer.Offset.X, 0);
            if (vm.Pages.Count > 0)
                PagesList.ScrollIntoView(vm.Pages[0]);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.CurrentPage))
        {
            SyncSidebarToCurrentPage(vm);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsSidebarVisible) && vm.IsSidebarVisible)
        {
            // A hidden ListBox cannot bring an item into view. Force a fresh scroll as soon as
            // the rail is shown again, even if the current page itself did not change.
            _lastSidebarIndex = -1;
            SyncSidebarToCurrentPage(vm, forceScroll: true);
        }
    }

    private void OnScrollToPageRequested(object? sender, int index)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (index < 0 || index >= vm.Pages.Count) return;
        PagesList.ScrollIntoView(vm.Pages[index]);
        SyncSidebarToCurrentPage(vm, forceScroll: true);
    }

    private void SyncSidebarToCurrentPage(MainWindowViewModel vm, bool forceScroll = false)
    {
        if (!vm.HasDocument || vm.Pages.Count == 0)
        {
            ThumbnailList.SelectedIndex = -1;
            _lastSidebarIndex = -1;
            return;
        }

        var index = Math.Clamp(vm.CurrentPage - 1, 0, vm.Pages.Count - 1);
        ThumbnailList.SelectedIndex = index;

        if (!vm.IsSidebarVisible)
        {
            // Do not remember a hidden rail as synchronized: when it is revealed we need one
            // real ScrollIntoView call to catch up with the document viewport.
            _lastSidebarIndex = -1;
            return;
        }

        if (!forceScroll && index == _lastSidebarIndex)
            return;

        ThumbnailList.ScrollIntoView(vm.Pages[index]);
        _lastSidebarIndex = index;
    }

    private async void OnPrintDialogRequested(object? sender, TaskCompletionSource<PrintOptions?> tcs)
    {
        var dialog = new PrintDialog();
        await dialog.ShowDialog(this);
        tcs.TrySetResult(dialog.IsCancelled ? null : dialog.Result);
    }

    private async void OpenPdfClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir documento PDF",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf", "*.PDF" } }
            }
        });

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (DataContext is MainWindowViewModel vm)
            await vm.OpenPdfAsync(path);
    }

    private async void SaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrWhiteSpace(vm.DocumentPath))
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Guardar cópia como",
            SuggestedFileName = vm.DocumentName,
            DefaultExtension = "pdf",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } }
            }
        });

        var destination = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(destination))
            return;

        try
        {
            File.Copy(vm.DocumentPath, destination, overwrite: true);
        }
        catch (Exception ex)
        {
            vm.ErrorText = $"Não foi possível guardar a cópia: {ex.Message}";
        }
    }

    private void FitWidthClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsInteractive)
            return;

        ApplyFitPreservingViewport(vm, () => vm.SetZoomToFitWidth(PageScrollViewer.Viewport.Width));
    }

    private void FitPageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsInteractive)
            return;

        ApplyFitPreservingViewport(
            vm,
            () => vm.SetZoomToFitPage(PageScrollViewer.Viewport.Width, PageScrollViewer.Viewport.Height));
    }

    /// <summary>
    /// Fit operations change every item's height, so leaving the numeric ScrollViewer offset
    /// untouched can move the viewport to a completely different page (often page 1). Capture
    /// the page under the viewport centre and the relative point inside it; once Avalonia reports
    /// the new extent, PageScrollViewerScrollChanged restores that logical anchor.
    /// </summary>
    private void ApplyFitPreservingViewport(MainWindowViewModel vm, Action fitAction)
    {
        var oldZoom = vm.Zoom;
        _pendingViewportAnchor = CaptureViewportAnchor(vm);
        fitAction();

        if (Math.Abs(vm.Zoom - oldZoom) < 0.0001)
            _pendingViewportAnchor = null;
    }

    private ViewportAnchor CaptureViewportAnchor(MainWindowViewModel vm)
    {
        var pageIndex = Math.Clamp(vm.CurrentPage - 1, 0, vm.Pages.Count - 1);
        var page = vm.Pages[pageIndex];
        var pageTop = GetPageTop(vm, pageIndex);
        var viewportCentre = PageScrollViewer.Offset.Y + PageScrollViewer.Viewport.Height / 2.0;
        var relative = (viewportCentre - pageTop) / Math.Max(1.0, page.DisplayHeight);
        return new ViewportAnchor(pageIndex, Math.Clamp(relative, 0.0, 1.0));
    }

    private void RestoreViewportAnchor(MainWindowViewModel vm, ViewportAnchor anchor)
    {
        if (anchor.PageIndex < 0 || anchor.PageIndex >= vm.Pages.Count)
            return;

        var page = vm.Pages[anchor.PageIndex];
        var pageTop = GetPageTop(vm, anchor.PageIndex);
        var desiredCentre = pageTop + page.DisplayHeight * anchor.RelativePosition;
        var targetY = Math.Max(0.0, desiredCentre - PageScrollViewer.Viewport.Height / 2.0);

        PageScrollViewer.Offset = new Vector(PageScrollViewer.Offset.X, targetY);
    }

    private static double GetPageTop(MainWindowViewModel vm, int pageIndex)
    {
        var top = PageListTopPadding;
        for (var i = 0; i < pageIndex; i++)
            top += vm.Pages[i].DisplayHeight + PageItemSpacing;
        return top;
    }

    private void ThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        // Ignore taps only until the first full-quality page has revealed the native reader.
        // After that, thumbnails/pages may continue filling in progressively in the background.
        if (sender is Control { Tag: int index } && DataContext is MainWindowViewModel vm && vm.IsInteractive)
            vm.GoToPage(index + 1);
    }

    private async void PdfPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border pageSurface ||
            pageSurface.DataContext is not PdfPageItemViewModel page ||
            DataContext is not MainWindowViewModel vm ||
            !vm.IsInteractive)
        {
            return;
        }

        var point = e.GetCurrentPoint(pageSurface);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var position = e.GetPosition(pageSurface);

        // Metadata is normally prepared as soon as the page is realized. A click that wins the
        // race simply awaits that same one-per-page task; it never causes a page rerender.
        if (!page.LinksLoaded)
            await vm.EnsurePageLinksAsync(page.Index);

        if (!page.TryGetLinkAtDisplayPoint(position.X, position.Y, out var link) || link is null)
            return;

        // Own this gesture completely so ListBox selection/scroll behaviour cannot also react.
        e.Handled = true;

        if (link.TargetPageIndex is int targetPageIndex)
        {
            vm.GoToPage(targetPageIndex + 1);
            return;
        }

        if (!string.IsNullOrWhiteSpace(link.Uri))
            OpenExternalPdfLink(vm, link.Uri);
    }

    private void PdfPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Border pageSurface ||
            pageSurface.DataContext is not PdfPageItemViewModel page)
        {
            return;
        }

        var position = e.GetPosition(pageSurface);
        pageSurface.Cursor = page.TryGetLinkAtDisplayPoint(position.X, position.Y, out _)
            ? LinkCursor
            : Cursor.Default;
    }

    private static void PdfPagePointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border pageSurface)
            pageSurface.Cursor = Cursor.Default;
    }

    private static void OpenExternalPdfLink(MainWindowViewModel vm, string rawUri)
    {
        var candidate = rawUri.Trim();
        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            vm.ErrorText = "O endereço deste link não é válido.";
            return;
        }

        // Only hand well-known external navigation schemes to the operating system. Internal
        // PDF destinations never reach this method; they are handled above with GoToPage().
        var supported = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals(Uri.UriSchemeFtp, StringComparison.OrdinalIgnoreCase);

        if (!supported)
        {
            vm.ErrorText = $"Tipo de link não suportado: {uri.Scheme}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            vm.ErrorText = $"Não foi possível abrir o link: {ex.Message}";
        }
    }

    private void PagesListContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.NotifyPagePrepared(e.Index);
    }

    private void PagesListContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && e.Container.DataContext is PdfPageItemViewModel page)
            vm.NotifyPageCleared(page.Index);
    }

    private void PageScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (_pendingViewportAnchor is { } anchor)
        {
            // This ScrollChanged is the layout/extent change caused by fit-to-width/page. Restore
            // the logical position first and do not let the temporary offset rewrite CurrentPage.
            _pendingViewportAnchor = null;
            RestoreViewportAnchor(vm, anchor);
            return;
        }

        vm.UpdateCurrentPageFromScrollOffset(
            PageScrollViewer.Offset.Y,
            PageScrollViewer.Viewport.Height,
            PageListTopPadding);
    }

    /// <summary>
    /// Up/Down = scroll the page list a small step, Left/Right = previous/next page - the four
    /// controls the user reaches for without touching the mouse. Plain keys only (no modifiers),
    /// so this never fights with the Ctrl+ shortcuts in <see cref="OnKeyDown"/> below, and it
    /// backs off while a text field has focus so it never hijacks ordinary typing.
    /// </summary>
    private void OnArrowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsInteractive) return;
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        switch (e.Key)
        {
            case Key.Down:
                PageScrollViewer.LineDown();
                e.Handled = true;
                break;
            case Key.Up:
                PageScrollViewer.LineUp();
                e.Handled = true;
                break;
            case Key.Right:
                if (vm.NextPageCommand.CanExecute(null)) vm.NextPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                if (vm.PreviousPageCommand.CanExecute(null)) vm.PreviousPageCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MainWindowViewModel vm) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl) return;

        switch (e.Key)
        {
            case Key.P:
                if (vm.PrintCommand.CanExecute(null)) vm.PrintCommand.Execute(null);
                e.Handled = true;
                break;
            // Zoom shortcuts are gated until the first page is ready. Once the reader is visible,
            // zooming only changes layout/scaling of already prepared page images; it never
            // restarts PDF rendering or background preparation.
            case Key.D0 or Key.NumPad0 when vm.IsInteractive:
                vm.ResetZoomCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add when vm.IsInteractive:
                vm.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract when vm.IsInteractive:
                vm.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void PageScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var zoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                           e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        // This Window-level tunnel handler must only take ownership of wheel gestures that
        // actually happen over the PDF viewport. Everywhere else, keep the native wheel action.
        if (!zoomModifier || !PageScrollViewer.IsPointerOver)
            return;

        // Consume Ctrl+wheel before any child ListBox/ScrollViewer gets a chance to scroll.
        // Set Handled before executing the command so layout work triggered by zoom can never
        // allow this same pointer event to continue down the routed-event pipeline.
        e.Handled = true;

        if (DataContext is not MainWindowViewModel vm || !vm.IsInteractive)
            return;

        // Native-reader convention: Ctrl+wheel up zooms in; Ctrl+wheel down zooms out.
        if (e.Delta.Y > 0)
            vm.ZoomInCommand.Execute(null);
        else if (e.Delta.Y < 0)
            vm.ZoomOutCommand.Execute(null);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var files = e.DataTransfer.TryGetFiles();
        var pdf = files?.FirstOrDefault(f =>
            string.Equals(Path.GetExtension(f.Name), ".pdf", StringComparison.OrdinalIgnoreCase));

        var path = pdf?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            await vm.OpenPdfAsync(path);
    }
}
