using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FileConverter.ViewModels;

namespace FileConverter.Views;

public partial class MainWindow : Window, IAppHost
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
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
