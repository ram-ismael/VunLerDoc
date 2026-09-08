using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VunLerDoc.Services;
using VunLerDoc.ViewModels;

namespace VunLerDoc.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ScrollToPageRequested += OnScrollToPageRequested;
                vm.PrintDialogRequested += OnPrintDialogRequested;
            }
        };

        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.RenderScaling = RenderScaling;
        };

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

        // Tunnel (not bubble) so this runs *before* PagesList - a focused ListBox has its own
        // built-in Up/Down = "move selection" behaviour, which would otherwise intercept the
        // arrow keys before our scroll/page-navigation handling ever saw them.
        AddHandler(KeyDownEvent, OnArrowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnScrollToPageRequested(object? sender, int index)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (index < 0 || index >= vm.Pages.Count) return;
        PagesList.ScrollIntoView(vm.Pages[index]);
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
        if (DataContext is MainWindowViewModel vm)
            vm.SetZoomToFitWidth(PageScrollViewer.Viewport.Width);
    }

    private void FitPageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.SetZoomToFitPage(PageScrollViewer.Viewport.Width, PageScrollViewer.Viewport.Height);
    }

    private void ThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: int index } && DataContext is MainWindowViewModel vm)
            vm.GoToPage(index + 1);
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
        if (DataContext is MainWindowViewModel vm)
            vm.UpdateCurrentPageFromScrollOffset(PageScrollViewer.Offset.Y);
    }

    /// <summary>
    /// Up/Down = scroll the page list a small step, Left/Right = previous/next page - the four
    /// controls the user reaches for without touching the mouse. Plain keys only (no modifiers),
    /// so this never fights with the Ctrl+ shortcuts in <see cref="OnKeyDown"/> below, and it
    /// backs off while a text field has focus so it never hijacks ordinary typing.
    /// </summary>
    private void OnArrowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.HasDocument) return;
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
            case Key.D0 or Key.NumPad0:
                vm.ResetZoomCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                vm.ZoomInCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                vm.ZoomOutCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void PageScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
        else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);
        e.Handled = true;
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
