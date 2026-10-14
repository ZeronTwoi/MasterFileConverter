using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FileConverter.ViewModels;

namespace FileConverter.Views;

public partial class MainWindow : Window, IAppHost
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // the list selects on its own; these follow what it did, and add dragging with Shift or Ctrl
        FileList.SelectionChanged += (_, _) => ViewModel?.SetSelection(
            FileList.Selection.SelectedIndexes.Order().Select(i => FileList.Items[i]).OfType<FileItemViewModel>().ToList());
        FileList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        FileList.AddHandler(PointerMovedEvent, OnRowDragged, RoutingStrategies.Bubble, handledEventsToo: true);
        FileList.AddHandler(PointerReleasedEvent, (_, _) => _dragKeys = KeyModifiers.None, RoutingStrategies.Bubble,
            handledEventsToo: true);
        // before the row takes the click for itself
        FileList.AddHandler(PointerPressedEvent, OnChipPressed, RoutingStrategies.Tunnel);
        ConfirmStop = () => ConfirmDialog.AskAsync(this,
            "Files are still being converted",
            "Closing now stops them. What is finished stays; a file that is not finished yet is not kept.",
            yes: "Stop and close", no: "Keep converting");
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is not { } viewModel) return;
        viewModel.Host = this;

        // open where the user left it
        var settings = viewModel.Settings;
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    // ---------- closing ----------
    //
    // Closing the window ends the app, and nothing of it is to go on afterwards: an encoder left
    // running would keep the processor busy with no window to show for it. So a batch that is
    // still running is stopped first — after asking, since it may be hours of work.

    /// <summary>The question put when the window is closed on a running batch. (Tests answer it themselves.)</summary>
    internal Func<Task<bool>> ConfirmStop { get; set; }

    private bool _asking;
    private bool _stopped;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            if (viewModel.IsConverting && !_stopped)
            {
                if (e.CloseReason == WindowCloseReason.WindowClosing)
                {
                    e.Cancel = true;
                    // once this close has been called off, not in the middle of it
                    Dispatcher.UIThread.Post(() => _ = AskThenCloseAsync(viewModel));
                    return;
                }
                // the system is shutting down: no time for questions, but the engines are ended
                viewModel.CancelAllCommand.Execute(null);
            }

            var settings = viewModel.Settings;
            settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                settings.WindowWidth = Width;
                settings.WindowHeight = Height;
            }
            viewModel.Flush();
            viewModel.InstallPendingUpdate();
        }
        base.OnClosing(e);
    }

    private async Task AskThenCloseAsync(MainViewModel viewModel)
    {
        if (_asking) return;
        _asking = true;
        try
        {
            if (viewModel.IsConverting && !await ConfirmStop()) return;
            await viewModel.StopConvertingAsync();
            _stopped = true;
            Close();
        }
        finally
        {
            _asking = false;
        }
    }

    // ---------- selecting several rows by dragging ----------
    //
    // Shift or Ctrl held while the left button goes down on a row, then moved over others with
    // the button still down: Shift selects from where the selection started to the row under
    // the pointer, as a Shift-click there would; Ctrl does to every row passed over what the
    // Ctrl-click did to the first — selects it, or takes it away again.

    private KeyModifiers _dragKeys;
    private bool _dragSelects;
    private int _dragIndex = -1;

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragKeys = KeyModifiers.None;
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed) return;
        _dragKeys = e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control);
        _dragIndex = RowAt(e.GetPosition(FileList));
        // the list has already answered the click: whether this row is selected now says what Ctrl-dragging does
        _dragSelects = _dragIndex >= 0 && FileList.Selection.IsSelected(_dragIndex);
    }

    private void OnRowDragged(object? sender, PointerEventArgs e)
    {
        if (_dragKeys == KeyModifiers.None) return;
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            _dragKeys = KeyModifiers.None;
            return;
        }
        int index = RowAt(e.GetPosition(FileList));
        if (index < 0 || index == _dragIndex) return;
        _dragIndex = index;

        var selection = FileList.Selection;
        selection.BeginBatchUpdate();
        try
        {
            if (_dragKeys.HasFlag(KeyModifiers.Shift))
            {
                int anchor = selection.AnchorIndex >= 0 ? selection.AnchorIndex : index;
                selection.Clear();
                selection.SelectRange(Math.Min(anchor, index), Math.Max(anchor, index));
                selection.AnchorIndex = anchor;
            }
            else if (_dragSelects)
            {
                selection.Select(index);
            }
            else
            {
                selection.Deselect(index);
            }
        }
        finally
        {
            selection.EndBatchUpdate();
        }
    }

    // ---------- a file's marks ----------
    //
    // A group's mark selects every file of the group, with its settings in the panel, to be seen
    // or changed together; the mark of a format's settings shows those, for every file of it.

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed) return;
        var chip = (e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("chip"));
        if (chip?.DataContext is not FileItemViewModel item) return;
        e.Handled = true;

        bool group = chip.Classes.Contains("groupchip");
        var files = group ? viewModel.FilesOfGroup(item.Group) : [item];
        var selection = FileList.Selection;
        selection.BeginBatchUpdate();
        try
        {
            selection.Clear();
            foreach (var file in files) selection.Select(viewModel.Items.IndexOf(file));
        }
        finally
        {
            selection.EndBatchUpdate();
        }
        viewModel.DetailedSettingsOpen = true;
        viewModel.Detail.OnlyThisFile = group;
    }

    /// <summary>The index of the row under a point of the list, or -1.</summary>
    private int RowAt(Avalonia.Point point)
    {
        var row = (FileList.InputHitTest(point) as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        return row == null ? -1 : FileList.IndexFromContainer(row);
    }

    // ---------- drag and drop ----------

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .ToList();
        if (paths.Count > 0) _ = ViewModel?.AddPathsAsync(paths);
        e.Handled = true;
    }

    // ---------- pickers ----------

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files to convert",
            AllowMultiple = true
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
