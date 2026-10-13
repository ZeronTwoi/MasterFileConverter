using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileConverter.Core;
using FileConverter.Core.Converters;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;

namespace FileConverter.ViewModels;

/// <summary>What the view model needs from the window it lives in.</summary>
public interface IAppHost
{
    Task<IReadOnlyList<string>> PickFilesAsync();
    Task<string?> PickFolderAsync(string title);
}

public partial class MainViewModel : ObservableObject
{
    private readonly ConversionService _service = new();
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _saveTimer;
    private CancellationTokenSource? _cts;
    private bool _loading = true;

    public IAppHost? Host { get; set; }

    /// <summary>The rows of the list. (A list that can take a thousand files as one change, not a thousand.)</summary>
    public AvaloniaList<FileItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private FileItemViewModel? _selectedItem;

    public IReadOnlyList<string> SpeedOptions { get; } =
        ["Fastest", "Fast", "Balanced", "Best quality"];

    public IReadOnlyList<string> ExistingFileOptions { get; } = ["Keep both", "Replace", "Skip"];

    [ObservableProperty]
    private int _sizePercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSpeedText))]
    private int _speedIndex = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSpeedText))]
    private bool _autoSpeed;

    [ObservableProperty]
    private string? _customOutputDirectory;

    /// <summary>Index into <see cref="ExistingFileOptions"/>; the values of <see cref="ExistingFileAction"/>.</summary>
    [ObservableProperty]
    private int _existingFileIndex;

    // context-aware combining: files sharing a combinable target become ONE output
    [ObservableProperty]
    private bool _combineFiles = true;

    [ObservableProperty]
    private string _mergeHint = "";

    [ObservableProperty]
    private bool _hasMergeableGroups;

    // one-click "set every compatible file to this target"
    [ObservableProperty]
    private IReadOnlyList<string> _bulkTargetChoices = [];

    [ObservableProperty]
    private string? _bulkTarget;

    [ObservableProperty]
    private bool _showBulkTarget;

    [ObservableProperty]
    private bool _isConverting;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _overallStatus = "";

    [ObservableProperty]
    private string _convertButtonText = "Convert all";

    [ObservableProperty]
    private bool _hasFinishedItems;

    [ObservableProperty]
    private bool _hasItems;

    // tool availability
    [ObservableProperty] private bool _ffmpegAvailable;
    [ObservableProperty] private bool _ffmpegComplete = true;
    [ObservableProperty] private string _ffmpegStatusDetail = "";
    [ObservableProperty] private bool _sevenZipAvailable;
    [ObservableProperty] private bool _officeEngineAvailable;
    [ObservableProperty] private bool _rarAvailable;
    [ObservableProperty] private bool _setupNeeded;
    [ObservableProperty] private bool _setupRunning;
    [ObservableProperty] private double _setupProgress;
    [ObservableProperty] private string _setupStatus = "";

    // GPU video encoding: on by default, used when a hardware encoder works and beats the CPU
    [ObservableProperty] private bool _useGpu = true;
    [ObservableProperty] private bool _gpuAvailable;
    [ObservableProperty] private bool _gpuInUse;
    [ObservableProperty] private string _gpuStatusText = "GPU encoder: checking…";
    [ObservableProperty] private string _gpuStatusDetail = "Testing which hardware video encoders work on this PC.";

    private (SpeedPreset Preset, string Description) _detected = PerformanceDetector.Detect();

    public string VersionText { get; } = "v" + AppInfo.Version;

    public string SizePercentText => SizePercent >= 100
        ? "Original quality (no size target)"
        : $"Target ≈ {SizePercent}% of original size";

    public string EffectiveSpeedText => AutoSpeed
        ? $"{SpeedOptions[(int)_detected.Preset]} for this PC ({_detected.Description})"
        : "";

    public string OutputDirectoryText => CustomOutputDirectory ?? "Same folder as each source file";
    public bool HasCustomOutputDirectory => CustomOutputDirectory != null;

    public string OfficeEngineTip =>
        "LibreOffice" + (PlatformServices.Current.OfficeAutomation is { } office ? $" or {office.Name}" : "") +
        " gives the best .doc/.docx/.pdf fidelity. Without one, document conversions fall back to text-level " +
        "conversion and legacy .doc input is unavailable.";

    /// <param name="store">Where settings are kept; the user's settings file by default.</param>
    /// <param name="findEngines">Look for FFmpeg and the other engines, and measure the GPU, in the background.</param>
    public MainViewModel(SettingsStore? store = null, bool findEngines = true)
    {
        _store = store ?? new SettingsStore();
        _settings = _store.Load();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => Flush();

        SizePercent = _settings.SizePercent;
        SpeedIndex = (int)_settings.Speed;
        AutoSpeed = _settings.AutoSpeed;
        UseGpu = _settings.UseGpu;
        CombineFiles = _settings.CombineFiles;
        ExistingFileIndex = (int)_settings.WhenFileExists;
        // a folder that has since disappeared (an unplugged drive) is not worth failing a batch over
        CustomOutputDirectory = Directory.Exists(_settings.OutputDirectory) ? _settings.OutputDirectory : null;
        Detail = new DetailPanelViewModel(this);
        DetailedSettingsOpen = _settings.DetailedSettings;
        _loading = false;

        Items.CollectionChanged += OnItemsChanged;
        if (findEngines) _ = FindEnginesAsync();
    }

    private async Task FindEnginesAsync()
    {
        await RefreshToolAvailabilityAsync();
        await DetectGpuAsync();
    }

    // ---------- settings that are remembered ----------

    public AppSettings Settings => _settings;

    /// <summary>Copies what the user changed into the settings and saves shortly afterwards.</summary>
    public void Persist()
    {
        if (_loading) return;
        _settings.SizePercent = SizePercent;
        _settings.Speed = (SpeedPreset)Math.Clamp(SpeedIndex, 0, 3);
        _settings.AutoSpeed = AutoSpeed;
        _settings.UseGpu = UseGpu;
        _settings.CombineFiles = CombineFiles;
        _settings.WhenFileExists = (ExistingFileAction)Math.Clamp(ExistingFileIndex, 0, 2);
        _settings.OutputDirectory = CustomOutputDirectory;

        // one write for a burst of changes (dragging the slider)
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes pending changes now; called when the window closes.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        _store.Save(_settings);
    }

    partial void OnSizePercentChanged(int value)
    {
        OnPropertyChanged(nameof(SizePercentText));
        Persist();
    }

    partial void OnSpeedIndexChanged(int value) => Persist();
    partial void OnAutoSpeedChanged(bool value) => Persist();
    partial void OnUseGpuChanged(bool value) => Persist();
    partial void OnExistingFileIndexChanged(int value) => Persist();

    partial void OnCombineFilesChanged(bool value)
    {
        Persist();
        RefreshContextUi();
    }

    partial void OnCustomOutputDirectoryChanged(string? value)
    {
        OnPropertyChanged(nameof(OutputDirectoryText));
        OnPropertyChanged(nameof(HasCustomOutputDirectory));
        Persist();
    }

    // ---------- appearance ----------

    public bool IsDarkTheme => _settings.Theme == "dark";
    public bool IsLightTheme => _settings.Theme == "light";
    public bool IsSystemTheme => _settings.Theme == "system";

    /// <summary>"dark", "light", or "system" to follow the desktop's own setting.</summary>
    [RelayCommand]
    private void SetTheme(string theme)
    {
        if (theme is not ("dark" or "light" or "system")) return;
        _settings.Theme = theme;
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsSystemTheme));
        if (Avalonia.Application.Current is App app) app.ApplyTheme(theme);
        Persist();
    }

    /// <summary>Opens the folder settings.json lives in.</summary>
    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            Flush(); // so the file is there to be seen
            PlatformServices.Current.Open(Path.GetDirectoryName(_store.FilePath)!);
        }
        catch (Exception ex)
        {
            OverallStatus = "Could not open the settings folder: " + ex.Message;
        }
    }

    /// <summary>Opens the folder with the app's licence and those of the libraries in it.</summary>
    [RelayCommand]
    private void OpenLicences()
    {
        try
        {
            PlatformServices.Current.Open(Path.Combine(AppContext.BaseDirectory, "licenses"));
        }
        catch (Exception ex)
        {
            OverallStatus = "Could not open the licences folder: " + ex.Message;
        }
    }

    // ---------- detailed settings ----------

    /// <summary>The Detailed Settings panel is showing.</summary>
    [ObservableProperty]
    private bool _detailedSettingsOpen;

    public DetailPanelViewModel Detail { get; }

    /// <summary>Passwords typed for a target format: used for this session, never written to disk.</summary>
    private readonly Dictionary<string, Dictionary<string, string>> _sessionSecrets = [];

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    /// <summary>What the panel is currently built for, so it is only rebuilt when that changes.</summary>
    private string _detailSignature = "";

    partial void OnDetailedSettingsOpenChanged(bool value)
    {
        if (!_loading)
        {
            _settings.DetailedSettings = value;
            Persist();
        }
        // opening the panel with nothing selected would show an empty panel next to a full list
        if (value && SelectedItem == null) SelectedItem = Items.FirstOrDefault(i => i.SelectedTarget != null);
        ShowDetail(force: true);
    }

    partial void OnSelectedItemChanged(FileItemViewModel? value) => ShowDetail();

    private void ShowDetail(bool force = false)
    {
        if (!DetailedSettingsOpen) return;
        var item = SelectedItem;
        var signature = item == null ? "" : $"{item.SourcePath}|{item.SelectedTarget}|{IsMerged(item)}";
        if (!force && signature == _detailSignature) return;
        _detailSignature = signature;
        Detail.Show(item);
    }

    /// <summary>The saved settings of a target format, with this session's passwords for it.</summary>
    public IReadOnlyDictionary<string, string> FormatValues(string target)
    {
        _settings.FormatSettings.TryGetValue(target, out var saved);
        if (!_sessionSecrets.TryGetValue(target, out var secrets) || secrets.Count == 0)
            return saved ?? NoValues;

        var merged = new Dictionary<string, string>(saved ?? NoValues);
        foreach (var pair in secrets) merged[pair.Key] = pair.Value;
        return merged;
    }

    /// <summary>Sets (or, with null, clears) one setting for every file with this target.</summary>
    public void SetFormatValue(string target, SettingDefinition definition, string? value)
    {
        var store = definition.IsSecret ? _sessionSecrets : _settings.FormatSettings;
        if (value == null)
        {
            if (store.TryGetValue(target, out var existing) && existing.Remove(definition.Key) && existing.Count == 0)
                store.Remove(target);
        }
        else
        {
            if (!store.TryGetValue(target, out var values)) store[target] = values = [];
            values[definition.Key] = value;
        }
        if (!definition.IsSecret) Persist();
    }

    /// <summary>Is this file about to be combined with others into one result?</summary>
    public bool IsMerged(FileItemViewModel item) =>
        CombineFiles && item.SelectedTarget is { } target && MergeConversion.CanMerge(item.Category, target) &&
        Items.Count(i => i.SelectedTarget == target && MergeConversion.CanMerge(i.Category, target)) >= 2;

    /// <summary>How many files are heading for each target they could be combined into.</summary>
    private Dictionary<string, int> CombinableCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var item in Items)
            if (item.SelectedTarget is { } target && MergeConversion.CanMerge(item.Category, target))
                counts[target] = counts.GetValueOrDefault(target) + 1;
        return counts;
    }

    /// <summary>The settings that mean something for a conversion, by source type, target and "combined".</summary>
    private readonly Dictionary<(string Extension, string Target, bool Merged), HashSet<string>> _applicableKeys = [];

    private HashSet<string> ApplicableKeys(string extension, string target, bool merged)
    {
        if (!_applicableKeys.TryGetValue((extension, target, merged), out var keys))
            _applicableKeys[(extension, target, merged)] = keys = SettingCatalog.For(extension, target, merged).Select(d => d.Key).ToHashSet();
        return keys;
    }

    /// <summary>
    /// The precise settings a file converts with: its target format's settings with its own laid
    /// over them, limited to the ones that mean something for this conversion (a resolution saved
    /// for PDF → JPG says nothing about photo → JPG).
    /// </summary>
    public DetailSettings EffectiveSettings(FileItemViewModel item, bool? merging = null)
    {
        if (item.SelectedTarget is not { } target) return DetailSettings.None;
        // nothing set for the format or the file: nothing to work out — the usual case, and in a
        // long list the one that must not cost a look at every other row
        var format = FormatValues(target);
        if (format.Count == 0 && item.Overrides.Count == 0) return DetailSettings.None;
        bool merged = merging ?? IsMerged(item);

        // a combined result belongs to no single file, so only the format's settings apply to it
        var all = DetailSettings.Merge(format, merged ? null : item.Overrides);
        if (all.IsEmpty) return all;

        var applicable = ApplicableKeys(item.Extension, target, merged);
        var kept = all.Values.Where(pair => applicable.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        return kept.Count == 0 ? DetailSettings.None : new DetailSettings(kept);
    }

    /// <summary>Called by the panel after any change: refreshes the "custom" marks in the list.</summary>
    public void OnDetailSettingsChanged() => RefreshCustomMarks();

    private void RefreshCustomMarks()
    {
        // counted once for the whole list, not once per row
        Dictionary<string, int>? combinable = null;
        foreach (var item in Items)
        {
            bool merged = false;
            if (CombineFiles && item.SelectedTarget is { } target && MergeConversion.CanMerge(item.Category, target))
                merged = (combinable ??= CombinableCounts()).GetValueOrDefault(target) >= 2;
            item.HasCustomSettings = !EffectiveSettings(item, merged).IsEmpty;
        }
    }

    // ---------- engines ----------

    /// <summary>
    /// Finds out in the background whether a GPU video encoder works here and whether it beats
    /// the CPU (measured once per machine setup, then remembered).
    /// </summary>
    private async Task DetectGpuAsync()
    {
        if (!FfmpegAvailable) return;
        try
        {
            var report = await Task.Run(() => HardwareEncoders.DetectAsync());
            var name = HardwareEncoders.Describe(report.Gpu);

            GpuAvailable = report.Usable;
            GpuInUse = report.Faster;
            if (report.Faster)
            {
                GpuStatusText = $"GPU encoder: {name}";
                GpuStatusDetail = $"{name} encodes video about {report.GpuFps / report.CpuFps:0.#}× faster than this CPU can sustain " +
                                  $"(≈{report.GpuFps:0} vs ≈{report.CpuFps:0} frames/s at 1080p), so it is used for video unless \"Best quality\" is selected.";
            }
            else if (report.Usable)
            {
                GpuStatusText = "GPU encoder: CPU is faster here";
                GpuStatusDetail = $"{name} works, but this CPU encodes about as fast (≈{report.CpuFps:0} vs ≈{report.GpuFps:0} frames/s at 1080p) " +
                                  "and compresses better, so ordinary video is encoded on the CPU. HDR video still uses the GPU.";
            }
            else
            {
                GpuStatusText = "GPU encoder: none";
                GpuStatusDetail = "No working hardware video encoder was found; video is encoded on the CPU.";
            }
            if (report.Hint != null)
            {
                // something the user can fix (an NVIDIA card waiting for a newer driver) — keep it in sight
                GpuStatusText = report.Faster
                    ? GpuStatusText + " · NVIDIA needs a driver update"
                    : "GPU encoder: NVIDIA needs a driver update";
                GpuStatusDetail = report.Hint + "\n\n" + GpuStatusDetail;
            }

            _detected = PerformanceDetector.Detect(report.Faster ? report.Gpu : GpuEncoder.None);
            OnPropertyChanged(nameof(EffectiveSpeedText));
        }
        catch
        {
            // detection is a convenience; conversions fall back to the CPU on their own
        }
    }

    private async Task RefreshToolAvailabilityAsync()
    {
        // looking along PATH and asking FFmpeg what it can encode both touch the disk — not on the UI thread
        var found = await Task.Run(() =>
        {
            bool ffmpeg = ToolLocator.FfmpegPath != null && ToolLocator.FfprobePath != null;
            return (
                Ffmpeg: ffmpeg,
                Outdated: ffmpeg ? null : ToolLocator.OutdatedFfmpeg,
                Missing: ffmpeg ? FfmpegCapabilities.MissingEssentials : [],
                SevenZip: ToolLocator.SevenZipPath != null,
                Office: ToolLocator.LibreOfficePath != null || ToolLocator.OfficeAutomationAvailable,
                Rar: ToolLocator.RarPath != null);
        });

        FfmpegAvailable = found.Ffmpeg;
        FfmpegComplete = found.Missing.Count == 0;
        FfmpegStatusDetail = found.Outdated != null
            ? $"The installed FFmpeg — {found.Outdated} — is older than the app works with " +
              $"({ToolLocator.MinimumFfmpegMajor} or newer).\n\"Set up engines\" downloads a current build."
            : found.Missing.Count == 0
                ? "Needed for audio and video conversions, and for gif ↔ video."
                : "This FFmpeg build is missing: " + string.Join("; ", found.Missing) +
                  ".\n\"Set up engines\" downloads a complete build.";
        SevenZipAvailable = found.SevenZip;
        OfficeEngineAvailable = found.Office;
        RarAvailable = found.Rar;
        SetupNeeded = !found.Ffmpeg || !found.SevenZip || found.Missing.Count > 0;
    }

    // ---------- the file list ----------

    partial void OnBulkTargetChanged(string? value)
    {
        if (value == null || _replacingBulkChoices) return;
        _bulkUpdate = true;
        try
        {
            foreach (var item in Items)
                if (item.State != ItemState.Converting && item.TargetOptions.Contains(value))
                    item.SelectedTarget = value;
        }
        finally
        {
            _bulkUpdate = false;
        }
        RefreshContextUi();
        // The box gives an order, it does not hold a state: it goes back to its placeholder once
        // the order is carried out, so that the same format can be chosen again later.
        Dispatcher.UIThread.Post(() => BulkTarget = null);
    }

    /// <summary>
    /// Set while the list of bulk targets is exchanged: the box then re-selects what it showed,
    /// which must not be taken for the user choosing it again (it used to undo a change made to
    /// a single row after "Set all to").
    /// </summary>
    private bool _replacingBulkChoices;

    // set while many items change at once, so the context UI is recomputed once instead of per item
    private bool _bulkUpdate;

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (FileItemViewModel item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;
        if (e.OldItems != null)
            foreach (FileItemViewModel item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;
        RefreshContextUi();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FileItemViewModel item) return;

        if (e.PropertyName == nameof(FileItemViewModel.SelectedTarget))
        {
            // remembered per source type: the next .png starts on the target this one ended on
            if (!_bulkUpdate && !_loading && item.SelectedTarget is { } target)
            {
                _settings.LastTargets[item.Extension] = target;
                Persist();
            }
            RefreshContextUi();
        }
        else if (e.PropertyName == nameof(FileItemViewModel.State) && !IsConverting)
        {
            // during a batch every row changes state several times, and the buttons this decides
            // are not shown; they are brought up to date once when it ends
            RefreshButtons();
        }
    }

    /// <summary>Recomputes the bulk-target choices and the "will be combined" hint.</summary>
    private void RefreshContextUi()
    {
        if (_bulkUpdate) return;
        HasItems = Items.Count > 0;
        ShowBulkTarget = Items.Count(i => i.TargetOptions.Count > 0) >= 2;
        var choices = Items.SelectMany(i => i.TargetOptions).Distinct().ToList();
        if (!choices.SequenceEqual(BulkTargetChoices))
        {
            _replacingBulkChoices = true;
            try
            {
                BulkTargetChoices = choices;
            }
            finally
            {
                _replacingBulkChoices = false;
            }
        }

        var groups = ComputeMergeGroups(Items.Where(i => i.SelectedTarget != null));
        HasMergeableGroups = groups.Count > 0;
        MergeHint = string.Join("   ·   ", groups.Select(g =>
        {
            var names = NaturalSort.SortPathsByFileName(g.Items.Select(i => i.SourcePath));
            var first = Path.GetFileName(names[0]);
            var last = Path.GetFileName(names[^1]);
            return $"{g.Items.Count} files → one {g.Target.ToUpperInvariant()}  ({first} → {last})";
        }));
        RefreshButtons();
        RefreshCustomMarks();
        ShowDetail();
    }

    private void RefreshButtons()
    {
        if (_bulkUpdate) return;
        var convertible = Items.Where(i => i.SelectedTarget != null).ToList();
        HasFinishedItems = Items.Any(i => i.State == ItemState.Done);
        ConvertButtonText =
            convertible.Any(i => i.State is ItemState.Pending or ItemState.Cancelled or ItemState.Skipped) ? "Convert all"
            : convertible.Any(i => i.State == ItemState.Failed) ? "Retry failed"
            : convertible.Count > 0 ? "Convert again"
            : "Convert all";
        ConvertAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Files sharing a combinable target (images → pdf/gif/tiff, anything → archive).</summary>
    private static List<(string Target, List<FileItemViewModel> Items)> ComputeMergeGroups(IEnumerable<FileItemViewModel> items) =>
        items.Where(i => i.SelectedTarget != null && MergeConversion.CanMerge(i.Category, i.SelectedTarget!))
             .GroupBy(i => i.SelectedTarget!)
             .Where(g => g.Count() >= 2)
             .Select(g => (g.Key, g.ToList()))
             .ToList();

    /// <summary>
    /// Adds files, and everything inside folders. A file of an unknown type that is added by
    /// itself is listed (it can at least be packed into an archive); inside a folder only the
    /// files the converter knows — pictures, audio, video, documents, archives — are taken.
    /// </summary>
    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var requested = paths.ToList();
        var (files, fromFolders) = await Task.Run(() => Expand(requested));
        if (files.Count == 0) return;

        var known = new HashSet<string>(Items.Select(i => i.SourcePath), PlatformServices.PathComparer);
        int skipped = 0;
        var added = new List<FileItemViewModel>(files.Count);
        foreach (var (path, size) in files)
        {
            if (!known.Add(path)) continue;

            var extension = FormatRegistry.GetExtension(path);
            if (fromFolders.Contains(path) && FormatRegistry.GetCategory(extension) == FileCategory.Other)
            {
                skipped++;
                continue;
            }

            _settings.LastTargets.TryGetValue(extension, out var preferred);
            var item = new FileItemViewModel(path, size, RarAvailable, preferred);
            if (item.TargetOptions.Count == 0)
            {
                item.StatusText = "Unsupported file type";
                item.State = ItemState.Failed;
            }
            added.Add(item);
        }

        _bulkUpdate = true;
        try
        {
            Items.AddRange(added); // one change for the list to show, however many files
        }
        finally
        {
            _bulkUpdate = false;
        }
        // with the settings panel open and nothing selected, it would sit empty beside a full list
        if (DetailedSettingsOpen && SelectedItem == null)
            SelectedItem = Items.FirstOrDefault(i => i.SelectedTarget != null);
        RefreshContextUi();
        if (skipped > 0 && !IsConverting)
            OverallStatus = $"{skipped} file(s) of other types in the folder were left out.";
    }

    /// <summary>The files behind what was added, with their sizes — read here, away from the window's thread.</summary>
    private static (List<(string Path, long Size)> Files, HashSet<string> FromFolders) Expand(IReadOnlyList<string> paths)
    {
        var files = new List<(string, long)>();
        var fromFolders = new HashSet<string>(PlatformServices.PathComparer);
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in NaturalSort.SortPathsByFileName(Directory.EnumerateFiles(path, "*", walk)))
                {
                    if (SizeOf(file) is not { } size) continue;
                    files.Add((file, size));
                    fromFolders.Add(file);
                }
            }
            else if (SizeOf(path) is { } size)
            {
                files.Add((path, size));
            }
        }
        return (files, fromFolders);
    }

    /// <summary>A file's size, or null when it is not there (any more) or cannot be looked at.</summary>
    private static long? SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the window for a file or folder. A desktop without a file-dialog service (a bare
    /// window manager on Linux) cannot show one; that is reported in the status line rather
    /// than allowed to take the app down.
    /// </summary>
    private async Task<T?> AskAsync<T>(Func<IAppHost, Task<T?>> ask) where T : class
    {
        if (Host == null) return null;
        try
        {
            return await ask(Host);
        }
        catch (Exception ex)
        {
            OverallStatus = $"No file dialog could be opened ({ex.Message}). Drop files onto the window instead.";
            return null;
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (await AskAsync<IReadOnlyList<string>>(async host => await host.PickFilesAsync()) is { } files)
            await AddPathsAsync(files);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (await AskAsync(host => host.PickFolderAsync("Add every file in a folder")) is { } folder)
            await AddPathsAsync([folder]);
    }

    [RelayCommand]
    private async Task ChooseOutputAsync()
    {
        if (await AskAsync(host => host.PickFolderAsync("Choose where converted files are saved")) is { } folder)
            CustomOutputDirectory = folder;
    }

    [RelayCommand]
    private void ResetOutput() => CustomOutputDirectory = null;

    [RelayCommand]
    private void RemoveItem(FileItemViewModel item)
    {
        if (item.State != ItemState.Converting)
            Items.Remove(item);
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (IsConverting) return;
        Items.Clear();
        OverallStatus = "";
    }

    [RelayCommand]
    private void ClearFinished()
    {
        _bulkUpdate = true;
        try
        {
            Items.RemoveAll(Items.Where(i => i.State == ItemState.Done).ToList());
        }
        finally
        {
            _bulkUpdate = false;
        }
        RefreshContextUi();
    }

    [RelayCommand]
    private void OpenResult(FileItemViewModel item)
    {
        if (item.OutputPath is { } path && (File.Exists(path) || Directory.Exists(path)))
            TryShell(() => PlatformServices.Current.Open(path), item);
    }

    [RelayCommand]
    private void ShowResultInFolder(FileItemViewModel item)
    {
        if (item.OutputPath is { } path && (File.Exists(path) || Directory.Exists(path)))
            TryShell(() => PlatformServices.Current.ShowInFileManager(path), item);
    }

    private void TryShell(Action action, FileItemViewModel item)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            OverallStatus = $"Could not open {Path.GetFileName(item.OutputPath)}: {ex.Message}";
        }
    }

    // ---------- converting ----------

    private bool CanConvert() => !IsConverting && Items.Any(i => i.SelectedTarget != null);

    /// <summary>The batch's settings, with the precise settings of this file's conversion on top.</summary>
    private ConversionOptions OptionsFor(FileItemViewModel item, bool merging = false) => new ConversionOptions
    {
        SizePercent = SizePercent,
        Speed = AutoSpeed ? _detected.Preset : (SpeedPreset)SpeedIndex,
        UseGpu = UseGpu
    }.WithDetail(EffectiveSettings(item, merging));

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertAllAsync()
    {
        var convertible = Items.Where(i => i.SelectedTarget != null && i.State != ItemState.Converting).ToList();
        // what still has no result; when everything is done already, the whole list again
        var pending = convertible.Where(i => i.NeedsConversion).ToList();
        if (pending.Count == 0) pending = convertible;
        if (pending.Count == 0) return;

        IsConverting = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var whenExists = (ExistingFileAction)Math.Clamp(ExistingFileIndex, 0, 2);
        var outputDirectory = CustomOutputDirectory;
        var watch = Stopwatch.StartNew();
        // output names are claimed up front so files converting side by side can't collide
        var reserved = new HashSet<string>(PlatformServices.PathComparer);

        // context-aware plan: files sharing a combinable target become one output
        var mergeGroups = CombineFiles
            ? ComputeMergeGroups(pending)
            : new List<(string Target, List<FileItemViewModel> Items)>();
        var mergedItems = mergeGroups.SelectMany(g => g.Items).ToHashSet();
        var singles = pending.Where(i => !mergedItems.Contains(i)).ToList();

        int totalUnits = singles.Count + mergeGroups.Count;
        int finished = 0, failed = 0, skipped = 0, cancelled = 0;
        OverallProgress = 0;
        OverallStatus = $"Converting 0 / {totalUnits} …";

        foreach (var item in pending)
        {
            item.State = ItemState.Pending;
            item.Progress = 0;
            item.OutputPath = null;
            item.StatusText = "Waiting…";
        }

        void UnitFinished()
        {
            finished++;
            OverallProgress = 100.0 * finished / totalUnits;
            OverallStatus = $"Converting {finished} / {totalUnits} …";
        }

        void Fail(IEnumerable<FileItemViewModel> items, Exception ex)
        {
            bool wasCancelled = ex is OperationCanceledException;
            if (wasCancelled) cancelled++; else failed++;
            foreach (var item in items)
            {
                item.State = wasCancelled ? ItemState.Cancelled : ItemState.Failed;
                item.Progress = 0;
                item.StatusText = ex switch
                {
                    OperationCanceledException => "Cancelled",
                    FriendlyException => ex.Message,
                    _ => "Failed: " + ex.Message
                };
            }
        }

        // Where every result goes is worked out before anything starts, and away from the window's
        // thread: it means looking at the disk once or twice per file, which a long list would feel.
        var plans = await Task.Run(() => singles.Select(item =>
        {
            try
            {
                if (outputDirectory != null) Directory.CreateDirectory(outputDirectory);
                var output = ConversionService.BuildOutputPath(item.SourcePath, item.SelectedTarget!, outputDirectory, reserved, whenExists);
                return (Item: item, Output: output, Exists: whenExists == ExistingFileAction.Skip && File.Exists(output), Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Item: item, Output: "", Exists: false, Error: ex);
            }
        }).ToList());

        var tasks = new List<Task>();
        tasks.AddRange(plans.Select(async plan =>
        {
            var (item, output, exists, error) = plan;
            try
            {
                if (error != null) throw error;
                if (exists)
                {
                    item.State = ItemState.Skipped;
                    item.StatusText = $"Skipped — {Path.GetFileName(output)} already exists";
                    skipped++;
                    return;
                }

                var progress = new Progress<double>(p =>
                {
                    if (item.State == ItemState.Pending) { item.State = ItemState.Converting; item.StatusText = "Converting…"; }
                    if (item.State == ItemState.Converting) item.Progress = p * 100;
                });

                var result = await _service.ConvertAsync(item.SourcePath, output, OptionsFor(item), progress, token);

                item.OutputPath = result.OutputPath;
                item.Progress = 100;
                item.State = ItemState.Done;
                item.StatusText = DescribeResult(result);
            }
            catch (Exception ex)
            {
                Fail([item], ex);
            }
            finally
            {
                UnitFinished();
            }
        }));

        tasks.AddRange(mergeGroups.Select(async group =>
        {
            foreach (var item in group.Items)
            {
                item.State = ItemState.Converting;
                item.StatusText = $"Combining {group.Items.Count} files…";
            }
            try
            {
                if (outputDirectory != null) Directory.CreateDirectory(outputDirectory);
                var progress = new Progress<double>(p =>
                {
                    foreach (var item in group.Items)
                        if (item.State == ItemState.Converting) item.Progress = p * 100;
                });

                var produced = await _service.ConvertManyAsync(
                    group.Items.Select(i => i.SourcePath).ToList(),
                    group.Target, OptionsFor(group.Items[0], merging: true), outputDirectory, progress, token, whenExists);

                if (produced == null)
                {
                    skipped++;
                    foreach (var item in group.Items)
                    {
                        item.State = ItemState.Skipped;
                        item.Progress = 0;
                        item.StatusText = "Skipped — the combined file already exists";
                    }
                    return;
                }

                var sizeText = FileItemViewModel.FormatSize(new FileInfo(produced).Length);
                foreach (var item in group.Items)
                {
                    item.OutputPath = produced;
                    item.Progress = 100;
                    item.State = ItemState.Done;
                    item.StatusText = $"Done — combined into {Path.GetFileName(produced)} ({sizeText})";
                }
            }
            catch (Exception ex)
            {
                Fail(group.Items, ex);
            }
            finally
            {
                UnitFinished();
            }
        }));

        await Task.WhenAll(tasks);

        int succeeded = totalUnits - failed - skipped - cancelled;
        var parts = new List<string> { $"{succeeded} converted" };
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        OverallStatus = $"Finished in {FormatDuration(watch.Elapsed)} — {string.Join(", ", parts)}.";
        OverallProgress = 100;
        IsConverting = false;
        _cts.Dispose();
        _cts = null;
        RefreshButtons();
    }

    private static string FormatDuration(TimeSpan time) =>
        time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min {time.Seconds} s" : $"{time.TotalSeconds:0.#} s";

    /// <summary>
    /// "Done — 2.3 MB" for files (plus how it was made, e.g. "· lossless copy");
    /// "Done — 34 files → name frames" for fan-out folders.
    /// </summary>
    private static string DescribeResult(ConversionResult result)
    {
        var produced = result.OutputPath;
        if (Directory.Exists(produced))
        {
            var count = Directory.GetFiles(produced).Length;
            return $"Done — {count} files → {Path.GetFileName(produced)}";
        }
        var text = $"Done — {FileItemViewModel.FormatSize(new FileInfo(produced).Length)}";
        return result.Note != null ? $"{text} · {result.Note}" : text;
    }

    [RelayCommand]
    private void CancelAll() => _cts?.Cancel();

    [RelayCommand]
    private async Task SetupToolsAsync()
    {
        if (SetupRunning) return;
        SetupRunning = true;
        try
        {
            if (ToolLocator.SevenZipPath == null)
            {
                SetupStatus = "Downloading 7-Zip…";
                await ToolLocator.DownloadSevenZipAsync(CancellationToken.None);
            }
            if (!FfmpegAvailable || !FfmpegComplete)
            {
                var summary = PlatformServices.Current.FfmpegDownloadSummary;
                SetupStatus = $"Downloading {summary}…";
                var progress = new Progress<double>(p =>
                {
                    SetupProgress = p * 100;
                    SetupStatus = $"Downloading {summary}… {p * 100:0}%";
                });
                await ToolLocator.DownloadFfmpegAsync(progress, CancellationToken.None);
            }
            SetupStatus = "Engines ready.";
        }
        catch (Exception ex)
        {
            SetupStatus = "Setup failed: " + ex.Message;
        }
        finally
        {
            SetupRunning = false;
        }
        await RefreshToolAvailabilityAsync();
        await DetectGpuAsync(); // FFmpeg may have just arrived
    }

    partial void OnIsConvertingChanged(bool value) => ConvertAllCommand.NotifyCanExecuteChanged();
}
