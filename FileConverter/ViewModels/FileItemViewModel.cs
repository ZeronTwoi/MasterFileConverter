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
    [NotifyPropertyChangedFor(nameof(IsSameFormat), nameof(FormatMarkText))]
    private string? _selectedTarget;

    /// <summary>Set to its own format: not converted into anything else, only compressed.</summary>
    public bool IsSameFormat => SelectedTarget != null && FormatRegistry.IsSameFormat(Extension, SelectedTarget);

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

    /// <summary>Settings set for this file alone, per target: a group belongs to one conversion, and comes back with it.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _own = new(StringComparer.Ordinal);

    /// <summary>
    /// Precise settings set for this file alone, for the target it is set to now; they sit on top
    /// of that format's settings. Set to another format, the file has none of its own there until
    /// some are set — and finds these again when it is set back.
    /// </summary>
    public Dictionary<string, string> Overrides
    {
        get
        {
            if (SelectedTarget is not { } target) return [];
            if (!_own.TryGetValue(target, out var values)) _own[target] = values = new(StringComparer.Ordinal);
            return values;
        }
    }

    /// <summary>This file converts with settings from the Format settings panel — its own, or its format's.</summary>
    [ObservableProperty]
    private bool _hasCustomSettings;

    /// <summary>
    /// Its settings group, 1 and up — files given settings of their own, the same settings, have
    /// the same number and colour — or 0 for a file that converts like every file of its format.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupText), nameof(HasGroup), nameof(HasFormatMark))]
    private int _group;

    public bool HasGroup => Group > 0;

    /// <summary>What the group's files share, said where the group's mark is pointed at.</summary>
    [ObservableProperty]
    private string _groupTip = "";

    public string GroupText => $"Group {Group}";

    /// <summary>
    /// Set while the file is in no group but its format has settings that every file converted to
    /// it gets: what they are, said where its mark is pointed at. Null otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormatMark), nameof(FormatMarkText))]
    private string? _formatTip;

    public bool HasFormatMark => FormatTip != null && Group == 0;

    public string FormatMarkText => $"{SelectedTarget?.ToUpperInvariant()} settings";

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
        // a file is only ever set to its own format by hand: compressing is a choice, not a default
        SelectedTarget = preferredTarget != null && TargetOptions.Contains(preferredTarget) &&
                         !FormatRegistry.IsSameFormat(Extension, preferredTarget)
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
