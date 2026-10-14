using CommunityToolkit.Mvvm.ComponentModel;
using FileConverter.Core;

namespace FileConverter.ViewModels;

public enum ItemState
{
    Pending,
    Converting,
    Done,
    Failed,
    Cancelled,
    /// <summary>The result already existed and the user chose not to replace it.</summary>
    Skipped
}

public partial class FileItemViewModel : ObservableObject
{
    public string SourcePath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public FileCategory Category { get; }
    public long SizeBytes { get; }
    public string SizeText { get; }
    public IReadOnlyList<string> TargetOptions { get; }

    [ObservableProperty]
    private string? _selectedTarget;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsFailed), nameof(IsConverting), nameof(HasResult), nameof(NeedsConversion))]
    private ItemState _state = ItemState.Pending;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _outputPath;

    /// <summary>Precise settings set for this file alone; they sit on top of its target format's settings.</summary>
    public Dictionary<string, string> Overrides { get; } = [];

    /// <summary>Shown as a small "custom" chip: this file converts with settings from the Format settings panel.</summary>
    [ObservableProperty]
    private bool _hasCustomSettings;

    public bool IsDone => State == ItemState.Done;
    public bool IsFailed => State == ItemState.Failed;
    public bool IsConverting => State == ItemState.Converting;

    /// <summary>There is a file or folder to open.</summary>
    public bool HasResult => State == ItemState.Done && OutputPath != null;

    /// <summary>Not converted yet, or the last attempt did not produce a result.</summary>
    public bool NeedsConversion => State is ItemState.Pending or ItemState.Failed or ItemState.Cancelled or ItemState.Skipped;

    /// <param name="size">The file's size in bytes, read by whoever found the file.</param>
    public FileItemViewModel(string path, long size, bool rarAvailable, string? preferredTarget = null)
    {
        SourcePath = path;
        FileName = Path.GetFileName(path);
        Extension = FormatRegistry.GetExtension(path);
        Category = FormatRegistry.GetCategory(Extension);
        SizeBytes = size;
        SizeText = FormatSize(SizeBytes);
        TargetOptions = FormatRegistry.GetTargets(Extension, rarAvailable);
        SelectedTarget = preferredTarget != null && TargetOptions.Contains(preferredTarget)
            ? preferredTarget
            : TargetOptions.FirstOrDefault();
    }

    /// <summary>A different target is a different job: whatever happened before no longer describes this row.</summary>
    partial void OnSelectedTargetChanged(string? value) => ResetResult();

    /// <summary>Back to "Ready" after a change that makes the previous result out of date.</summary>
    public void ResetResult()
    {
        if (State == ItemState.Converting) return;
        if (TargetOptions is { Count: 0 }) return; // an unsupported file stays marked as such
        State = ItemState.Pending;
        Progress = 0;
        StatusText = "Ready";
        OutputPath = null;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B"
    };
}
