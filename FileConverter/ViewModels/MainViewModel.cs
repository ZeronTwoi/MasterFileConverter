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

    /// <summary>Closes the window, and with it the app.</summary>
    void Close();
}

/// <summary>What the window needs of the updater. (Tests put their own in its place: they neither go online nor replace anything.)</summary>
public interface IAppUpdates
{
    /// <summary>Can this copy of the app replace itself? If not, a new version is a link to the download page.</summary>
    bool CanUpdate { get; }

    Task<AvailableUpdate?> FindAsync(CancellationToken ct);
    Task<PreparedUpdate> PrepareAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct);
    void Start(PreparedUpdate update, bool restart);
    void Discard();
}

/// <summary>The updater itself, see <see cref="AppUpdater"/>.</summary>
internal sealed class AppUpdates : IAppUpdates
{
    public bool CanUpdate => AppUpdater.CanUpdate;
    public Task<AvailableUpdate?> FindAsync(CancellationToken ct) => AppUpdater.FindAsync(ct);

    public Task<PreparedUpdate> PrepareAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct) =>
        AppUpdater.PrepareAsync(update, progress, ct);

    public void Start(PreparedUpdate update, bool restart) => AppUpdater.Start(update, restart);
    public void Discard() => AppUpdater.Discard();
}

/// <summary>Where an update the user asked for has got to.</summary>
public enum UpdateStage
{
    None,

    /// <summary>Being downloaded and checked.</summary>
    Fetching,

    /// <summary>Fetched; the app has to close for it to be put in place.</summary>
    Ready,
    Failed
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
    [NotifyPropertyChangedFor(nameof(EffectiveSpeedText), nameof(ResultLevel), nameof(ResultText))]
    private int _speedIndex = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveSpeedText), nameof(ResultLevel), nameof(ResultText))]
    private bool _autoSpeed;

    /// <summary>
    /// The Result slider: the speed in plain words, from 0 (done soonest) to 3 (the best result
    /// for its size). It shows the speed in force — the one chosen for this PC while Auto is
    /// on — and moving it chooses that speed by hand. Speed only ever trades time against how
    /// small a file can be made at a given look, so it does not compete with the File size slider.
    /// </summary>
    public int ResultLevel
    {
        get => (int)SpeedInForce;
        set
        {
            value = Math.Clamp(value, 0, SpeedOptions.Count - 1);
            if (value == ResultLevel) return;
            AutoSpeed = false;
            SpeedIndex = value;
        }
    }

    /// <summary>What the Result slider's position means, said under it.</summary>
    public string ResultText => (ResultLevel switch
    {
        0 => "Done soonest, larger files",
        1 => "Quick, slightly larger files",
        2 => "Balanced",
        _ => "Best for its size, takes longest"
    }) + (AutoSpeed ? " (chosen for this PC)" : "");

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

    // the same for the files selected alone, when there are several
    [ObservableProperty]
    private IReadOnlyList<TargetChoice> _selectionTargetChoices = [];

    [ObservableProperty]
    private TargetChoice? _selectionTarget;

    [ObservableProperty]
    private bool _showSelectionTarget;

    [ObservableProperty]
    private string _selectionText = "";

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
        " lays out Word, Excel and PowerPoint files (and their OpenDocument counterparts) exactly as they are. " +
        "Without one, the built-in converter keeps their headings, lists, tables and pictures; the old .doc and .ppt " +
        "formats, drawings (.odg) and Publisher files need an office program.";

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

    /// <summary>Opens the page the app comes from: its source, its downloads, where to report a problem.</summary>
    [RelayCommand]
    private void OpenWebsite()
    {
        try
        {
            PlatformServices.Current.Open(AppInfo.Website);
        }
        catch (Exception ex)
        {
            OverallStatus = "Could not open the website: " + ex.Message;
        }
    }

    // ---------- new versions ----------
    //
    // Nothing here connects anywhere until the user has said yes to being told about new
    // versions, or asks for a look from the menu. After a "no" the question is not put again and
    // no connection is made by itself; after a "yes" the app looks once at each start — see
    // UpdateCheck. A newer version is only ever fetched and installed on a click: see "updating".

    /// <summary>How the newest released version is learnt. (Tests put their own answer here: they stay offline.)</summary>
    internal Func<CancellationToken, Task<Version?>> FindLatestVersion { get; set; } = UpdateCheck.FindLatestVersionAsync;

    /// <summary>What fetches a newer version and puts it in place.</summary>
    internal IAppUpdates Updates { get; set; } = new AppUpdates();

    private bool _lookedForUpdate;

    /// <summary>The question "tell you about new versions?" is showing, because it has never been answered.</summary>
    [ObservableProperty]
    private bool _askAboutUpdates;

    /// <summary>A version newer than this one ("2.4.0"), once a look has found one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private string? _newVersion;

    public bool UpdateAvailable => NewVersion != null;

    public string UpdateText => Updates.CanUpdate
        ? $"Version {NewVersion} is available — update now"
        : $"Version {NewVersion} is available — open the download page";

    /// <summary>The user wants to be told about new versions.</summary>
    public bool NotifyAboutUpdates => _settings.NotifyAboutUpdates == true;

    /// <summary>
    /// Called once, when the app has started as a window: puts the question the first time, and
    /// from then on looks for a new version if — and only if — the answer was yes.
    /// </summary>
    public void BeginUpdateNotices()
    {
        if (_settings.NotifyAboutUpdates == null) AskAboutUpdates = true;
        else if (_settings.NotifyAboutUpdates == true) _ = LookForUpdateAsync();
    }

    /// <summary>The answer to the question: "yes" or "no".</summary>
    [RelayCommand]
    private void AnswerUpdateQuestion(string answer) => SetUpdateNotices(answer == "yes");

    /// <summary>The same choice, changed later from the menu.</summary>
    [RelayCommand]
    private void ToggleUpdateNotices() => SetUpdateNotices(!NotifyAboutUpdates);

    private void SetUpdateNotices(bool wanted)
    {
        _settings.NotifyAboutUpdates = wanted;
        AskAboutUpdates = false;
        OnPropertyChanged(nameof(NotifyAboutUpdates));
        Persist();
        if (wanted) _ = LookForUpdateAsync();
        else if (UpdateStage == UpdateStage.None) NewVersion = null;
    }

    private async Task LookForUpdateAsync()
    {
        if (_lookedForUpdate) return; // once per start, however often the choice is switched
        _lookedForUpdate = true;
        try
        {
            var latest = await FindLatestVersion(CancellationToken.None);
            if (latest != null && NotifyAboutUpdates && UpdateCheck.IsNewer(latest, AppInfo.Version))
                NewVersion = latest.ToString();
        }
        catch
        {
            // a look that fails is no news; the next start looks again
        }
    }

    /// <summary>One look, asked for from the menu — also by someone who does not want the app to look by itself.</summary>
    [RelayCommand]
    private async Task LookForUpdateNowAsync()
    {
        OverallStatus = "Looking for a new version…";
        Version? latest = null;
        try
        {
            latest = await FindLatestVersion(CancellationToken.None);
        }
        catch
        {
            // said below
        }

        if (latest == null)
        {
            OverallStatus = "The newest version could not be looked up. Is this computer online?";
        }
        else if (UpdateCheck.IsNewer(latest, AppInfo.Version))
        {
            NewVersion = latest.ToString();
            OverallStatus = $"Version {NewVersion} is available.";
        }
        else
        {
            OverallStatus = $"This is the newest version ({AppInfo.Version}).";
        }
    }

    [RelayCommand]
    private void OpenDownloadPage()
    {
        try
        {
            PlatformServices.Current.Open(UpdateCheck.DownloadPage);
        }
        catch (Exception ex)
        {
            OverallStatus = "Could not open the download page: " + ex.Message;
        }
    }

    // ---------- updating ----------
    //
    // One click fetches the new version, checks it and installs it. The app has to close for
    // that, so it only closes by itself when nothing would be lost: no files in the list, nothing
    // converting. Otherwise the update waits, ready, for "Restart and update" — or for the app to
    // be closed, which installs it too.

    private CancellationTokenSource? _updateCts;
    private PreparedUpdate? _preparedUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBannerVisible))]
    [NotifyPropertyChangedFor(nameof(UpdateFetching))]
    [NotifyPropertyChangedFor(nameof(UpdateReady))]
    [NotifyPropertyChangedFor(nameof(UpdateFailed))]
    private UpdateStage _updateStage;

    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty] private double _updateProgress;

    public bool UpdateBannerVisible => UpdateStage != UpdateStage.None;
    public bool UpdateFetching => UpdateStage == UpdateStage.Fetching;
    public bool UpdateReady => UpdateStage == UpdateStage.Ready;
    public bool UpdateFailed => UpdateStage == UpdateStage.Failed;

    /// <summary>Closing the app now would lose something: a list of files, a running conversion or download.</summary>
    private bool HasWorkInHand => HasItems || IsConverting || SetupRunning;

    /// <summary>The warning sign and its menu entry: update — or, for a copy that cannot replace itself, the download page.</summary>
    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (!Updates.CanUpdate)
        {
            OpenDownloadPage();
            return;
        }
        if (UpdateStage == UpdateStage.Fetching) return;
        if (UpdateStage == UpdateStage.Ready)
        {
            if (RestartForUpdateCommand.CanExecute(null)) RestartForUpdate();
            return;
        }

        var cts = _updateCts = new CancellationTokenSource();
        UpdateProgress = 0;
        UpdateTitle = $"Updating to version {NewVersion}";
        UpdateDetail = "Looking up the download…";
        UpdateStage = UpdateStage.Fetching;
        try
        {
            var update = await Updates.FindAsync(cts.Token);
            if (update == null)
            {
                UpdateStage = UpdateStage.None;
                NewVersion = null;
                OverallStatus = $"This is the newest version ({AppInfo.Version}).";
                return;
            }

            UpdateTitle = $"Updating to version {update.Version}";
            var progress = new Progress<double>(done =>
            {
                if (UpdateStage != UpdateStage.Fetching) return;
                UpdateProgress = done * 100;
                UpdateDetail = done < 1 ? $"Downloading… {done * 100:0}%" : "Checking the download…";
            });
            _preparedUpdate = await Updates.PrepareAsync(update, progress, cts.Token);

            if (HasWorkInHand)
            {
                UpdateTitle = $"Version {update.Version} is ready to install";
                UpdateDetail = "The app restarts for it, and the list of files is not kept. Or finish first: closing the app installs it too.";
                UpdateStage = UpdateStage.Ready;
            }
            else
            {
                InstallUpdate(restart: true);
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStage = UpdateStage.None;
        }
        catch (Exception ex)
        {
            UpdateFailedBecause(ex.Message);
        }
        finally
        {
            _updateCts = null;
            cts.Dispose();
        }
    }

    [RelayCommand]
    private void CancelUpdate() => _updateCts?.Cancel();

    private bool CanRestartForUpdate() => UpdateStage == UpdateStage.Ready && !IsConverting && !SetupRunning;

    [RelayCommand(CanExecute = nameof(CanRestartForUpdate))]
    private void RestartForUpdate() => InstallUpdate(restart: true);

    /// <summary>Takes the banner away: a failure that has been read, or an update that is not wanted after all.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        if (UpdateStage == UpdateStage.Fetching) return;
        if (_preparedUpdate != null)
        {
            _preparedUpdate = null;
            Updates.Discard();
        }
        UpdateStage = UpdateStage.None;
    }

    /// <summary>Called as the window closes: an update that is ready goes in now, without starting the app again.</summary>
    public void InstallPendingUpdate()
    {
        if (UpdateStage == UpdateStage.Ready) InstallUpdate(restart: false);
    }

    private void InstallUpdate(bool restart)
    {
        if (_preparedUpdate is not { } update) return;
        try
        {
            Flush();
            Updates.Start(update, restart);
            _preparedUpdate = null;
            UpdateStage = UpdateStage.None;
            if (restart) Host?.Close();
        }
        catch (Exception ex)
        {
            UpdateFailedBecause(ex.Message);
        }
    }

    private void UpdateFailedBecause(string reason)
    {
        if (_preparedUpdate != null)
        {
            _preparedUpdate = null;
            Updates.Discard();
        }
        UpdateTitle = "The update could not be installed";
        UpdateDetail = reason;
        UpdateStage = UpdateStage.Failed;
    }

    /// <summary>
    /// Called once the window is up: says so when the app has just been updated, or why the last
    /// update could not be put in place.
    /// </summary>
    public void AfterStart(string? updatedTo, string? updateFailure)
    {
        if (updateFailure != null) UpdateFailedBecause(updateFailure);
        else if (updatedTo != null) OverallStatus = $"Updated to version {updatedTo}.";
    }

    partial void OnUpdateStageChanged(UpdateStage value) => RestartForUpdateCommand.NotifyCanExecuteChanged();
    partial void OnSetupRunningChanged(bool value) => RestartForUpdateCommand.NotifyCanExecuteChanged();

    // ---------- format settings ----------

    /// <summary>The Format settings panel is showing.</summary>
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

    /// <summary>Every row selected in the list, in list order, as the list last said.</summary>
    private IReadOnlyList<FileItemViewModel> _selection = [];

    /// <summary>
    /// The rows the panel is for: all those selected, or the one in <see cref="SelectedItem"/>
    /// when that is set some other way than by selecting in the list.
    /// </summary>
    public IReadOnlyList<FileItemViewModel> SelectedItems =>
        SelectedItem == null ? [] : _selection.Count > 1 && _selection.Contains(SelectedItem) ? _selection : [SelectedItem];

    /// <summary>Called by the window whenever the list's selection changes.</summary>
    public void SetSelection(IReadOnlyList<FileItemViewModel> items)
    {
        _selection = items;
        RefreshSelectionTarget();
        ShowDetail();
    }

    private void ShowDetail(bool force = false)
    {
        if (!DetailedSettingsOpen) return;
        var items = SelectedItems;
        var signature = items.Count == 0
            ? ""
            : $"{IsMerged(items[0])}|" + string.Join("\n", items.Select(i => i.SourcePath + "|" + i.SelectedTarget));
        if (!force && signature == _detailSignature) return;
        _detailSignature = signature;
        Detail.Show(items);
    }

    /// <summary>
    /// Can these rows have their settings set together — are they converted the same way, into
    /// the same format with the same settings to choose from? (mov → mp4 and mkv → mp4 are;
    /// mov → mp4 and png → jpg are not, nor a photo → jpg and a PDF → jpg.)
    /// </summary>
    public bool ConvertTheSameWay(IReadOnlyList<FileItemViewModel> items)
    {
        if (items.Count == 0 || items[0].SelectedTarget is not { } target) return false;
        var keysOf = new Dictionary<string, string>();
        string Keys(FileItemViewModel item)
        {
            if (!keysOf.TryGetValue(item.Extension, out var keys))
                keysOf[item.Extension] = keys = string.Join(",", SettingsOf(item.Extension, target, false).All.Select(d => d.Key));
            return keys;
        }
        var first = Keys(items[0]);
        return items.All(i => i.SelectedTarget == target && Keys(i) == first);
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

    /// <summary>The files of the list that are combined with others into one result, as last worked out.</summary>
    private HashSet<FileItemViewModel> _merged = [];

    /// <summary>Is this file about to be combined with others into one result?</summary>
    public bool IsMerged(FileItemViewModel item) => _merged.Contains(item);

    /// <summary>
    /// The results that combine several files, when files are combined: one for each target the
    /// files can be combined into — and within it one for each group, so that files given
    /// settings of their own are combined by themselves, with those settings. Two files at least
    /// make one.
    /// </summary>
    private List<(string Target, List<FileItemViewModel> Items)> MergePlan(IEnumerable<FileItemViewModel> items) =>
        items
            .Where(i => i.SelectedTarget is { } target && MergeConversion.CanMerge(i.Category, target, i.Extension))
            .GroupBy(i => i.SelectedTarget + "\n" + Signature(EffectiveSettings(i, merging: true)), StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .Select(g => (g.First().SelectedTarget!, g.ToList()))
            .ToList();

    /// <summary>Settings as one line of text, the same for the same settings whatever order they were set in.</summary>
    private static string Signature(DetailSettings settings) =>
        string.Join("\n", settings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    /// <summary>The settings a conversion has, and which of them the panel can set, by source type, target and "combined".</summary>
    private readonly Dictionary<(string Extension, string Target, bool Merged), (IReadOnlyList<SettingDefinition> All, HashSet<string> Applicable)> _settingsOf = [];

    private (IReadOnlyList<SettingDefinition> All, HashSet<string> Applicable) SettingsOf(string extension, string target, bool merged)
    {
        if (!_settingsOf.TryGetValue((extension, target, merged), out var settings))
        {
            var all = SettingCatalog.For(extension, target, merged);
            // what has no row in the panel is not in force either — the Speed setting decides it
            // now, whatever an older version saved — except what a way of setting the size stands for
            var applicable = all.Where(d => d.InPanel || d.Mode != null).Select(d => d.Key).ToHashSet();
            _settingsOf[(extension, target, merged)] = settings = (all, applicable);
        }
        return settings;
    }

    /// <summary>
    /// The precise settings a file converts with: its target format's settings with its own laid
    /// over them, limited to the ones that mean something for this conversion (a resolution saved
    /// for PDF → JPG says nothing about photo → JPG), and of the size and quality settings only
    /// those of the way chosen.
    /// </summary>
    /// <param name="own">False: as it would convert without settings of its own.</param>
    public DetailSettings EffectiveSettings(FileItemViewModel item, bool? merging = null, bool own = true)
    {
        if (item.SelectedTarget is not { } target) return DetailSettings.None;
        // nothing set for the format or the file: nothing to work out — the usual case, and in a
        // long list the one that must not cost a look at every other row
        var format = FormatValues(target);
        var overrides = own ? item.Overrides : NoValues;
        if (format.Count == 0 && overrides.Count == 0) return DetailSettings.None;
        bool merged = merging ?? IsMerged(item);

        // files combined into one result are combined with files of the same settings only, so a
        // file's own settings are those of the result it is part of
        var all = DetailSettings.Merge(format, overrides);
        if (all.IsEmpty) return all;

        var (definitions, applicable) = SettingsOf(item.Extension, target, merged);
        var kept = SettingCatalog.Resolve(definitions, all.Values)
            .Where(pair => applicable.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        return kept.Count == 0 ? DetailSettings.None : new DetailSettings(kept);
    }

    /// <summary>
    /// Formats in the list whose size and quality are set in Format settings, so the File size
    /// slider does not decide them — said under the slider. Null when it decides for every file.
    /// </summary>
    [ObservableProperty]
    private string? _sliderExceptionText;

    /// <summary>
    /// Called by the panel after any change: refreshes the marks in the list — and the panel
    /// itself, should the change have taken its files out of a combined result or into one.
    /// </summary>
    public void OnDetailSettingsChanged()
    {
        RefreshCustomMarks();
        ShowDetail();
    }

    /// <summary>
    /// The groups whose files are converted the same way as this one (and so can share its
    /// settings), by number, with how many files each has.
    /// </summary>
    public IReadOnlyList<(int Number, int Count)> GroupsLike(FileItemViewModel item) =>
        Items.Where(i => i.Group > 0 && ConvertTheSameWay([item, i]))
            .GroupBy(i => i.Group).OrderBy(g => g.Key)
            .Select(g => (g.Key, Items.Count(i => i.Group == g.Key))).ToList();

    /// <summary>Every file of a group, in list order.</summary>
    public IReadOnlyList<FileItemViewModel> FilesOfGroup(int number) =>
        number <= 0 ? [] : Items.Where(i => i.Group == number).ToList();

    /// <summary>
    /// Puts files in a group — gives them the settings of their own that its files have — or,
    /// with 0, in none: takes their settings of their own away.
    /// </summary>
    public void PutInGroup(IReadOnlyList<FileItemViewModel> items, int number)
    {
        var model = number > 0 && items.Count > 0
            ? Items.FirstOrDefault(i => i.Group == number && ConvertTheSameWay([items[0], i]))
            : null;
        if (number > 0 && model == null) return;
        var settings = model?.Overrides.ToList() ?? [];
        foreach (var item in items)
        {
            if (item == model) continue;
            item.Overrides.Clear();
            foreach (var (key, value) in settings) item.Overrides[key] = value;
        }
        OnDetailSettingsChanged();
    }

    /// <summary>
    /// The number each group of settings had when the marks were last worked out, so that a group
    /// keeps its number and colour for as long as it is there.
    /// </summary>
    private Dictionary<string, int> _groupNumbers = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks the rows that convert with Format settings. Files given settings of their own that
    /// make them convert unlike the rest of their format go into numbered, coloured groups — the
    /// same settings, the same group — so several groups can be told apart at a glance; files
    /// that only have what every file of their format has get a plain mark that says so. Also
    /// works out which files are combined, and says under the File size slider which files it
    /// does not decide.
    /// </summary>
    private void RefreshCustomMarks()
    {
        var plan = MergePlan(Items);
        var merged = CombineFiles ? plan.SelectMany(g => g.Items).ToHashSet() : [];
        _merged = merged;

        // the files of each group, in list order, with the number each had before
        var groups = new Dictionary<string, List<(FileItemViewModel Item, int Before)>>(StringComparer.Ordinal);
        var tips = new Dictionary<string, (string Target, IReadOnlyList<SettingDefinition> Definitions, DetailSettings Settings)>(StringComparer.Ordinal);
        var perTarget = new SortedDictionary<string, (int All, int Own)>(StringComparer.Ordinal);
        foreach (var item in Items)
        {
            int before = item.Group;
            if (item.SelectedTarget is not { } target)
            {
                item.HasCustomSettings = false;
                item.FormatTip = null;
                item.Group = 0;
                continue;
            }
            bool isMerged = merged.Contains(item);
            var effective = EffectiveSettings(item, isMerged);
            var definitions = SettingsOf(item.Extension, target, isMerged).All;
            item.HasCustomSettings = !effective.IsEmpty;

            var name = target.ToUpperInvariant();
            var counts = perTarget.GetValueOrDefault(name);
            bool replaces = !effective.IsEmpty && SettingCatalog.ReplacesSlider(definitions, effective.Values);
            perTarget[name] = (counts.All + 1, counts.Own + (replaces ? 1 : 0));

            // in a group only when what it was given of its own changes anything
            var signature = Signature(effective);
            var formatOnly = item.Overrides.Count == 0 ? effective : EffectiveSettings(item, isMerged, own: false);
            if (item.Overrides.Count == 0 || signature == Signature(formatOnly))
            {
                item.Group = 0;
                item.FormatTip = effective.IsEmpty ? null : DescribeFormatSettings(name, definitions, effective);
                continue;
            }
            item.FormatTip = null;
            var key = target + "\n" + signature;
            if (!groups.TryGetValue(key, out var members))
            {
                groups[key] = members = [];
                tips[key] = (name, definitions, effective);
            }
            members.Add((item, before));
        }

        var numbers = NumberGroups(groups);
        foreach (var (key, members) in groups)
        {
            var (target, definitions, settings) = tips[key];
            var tip = DescribeGroup(numbers[key], target, definitions, settings);
            foreach (var (item, _) in members)
            {
                item.GroupTip = tip;
                item.Group = numbers[key];
            }
        }

        var exceptions = perTarget.Where(p => p.Value.Own > 0)
            .Select(p => p.Value.Own == p.Value.All ? p.Key : $"{p.Value.Own} of {p.Value.All} {p.Key} files").ToList();
        SliderExceptionText = exceptions.Count == 0 ? null : $"Not for {string.Join(", ", exceptions)}: set in Format settings";
        RefreshMergeHint(plan);
    }

    /// <summary>
    /// Numbers the groups so that none changes its number under the user's eyes: a group whose
    /// settings are as they were keeps its number; so does one whose settings were changed for all
    /// its files at once (the number most of them had); a new group takes the lowest number free.
    /// </summary>
    private Dictionary<string, int> NumberGroups(Dictionary<string, List<(FileItemViewModel Item, int Before)>> groups)
    {
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        foreach (var key in groups.Keys)
            if (_groupNumbers.TryGetValue(key, out var number) && taken.Add(number))
                numbers[key] = number;
        foreach (var (key, members) in groups)
        {
            if (numbers.ContainsKey(key)) continue;
            int had = members.Select(m => m.Before).Where(n => n > 0 && !taken.Contains(n))
                .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (had > 0 && taken.Add(had)) numbers[key] = had;
        }
        foreach (var key in groups.Keys)
        {
            if (numbers.ContainsKey(key)) continue;
            int free = 1;
            while (taken.Contains(free)) free++;
            taken.Add(free);
            numbers[key] = free;
        }
        _groupNumbers = numbers;
        return numbers;
    }

    /// <summary>"Set by: Own file size · File size (%): 50 · Maximum width (px): 1280".</summary>
    private static string DescribeSettings(IReadOnlyList<SettingDefinition> definitions, DetailSettings settings) =>
        string.Join(" · ", settings.Values.OrderBy(p => IndexOf(definitions, p.Key)).Select(p =>
        {
            var definition = definitions.FirstOrDefault(d => d.Key == p.Key);
            if (definition == null) return p.Key;
            if (definition.IsSecret) return $"{definition.Label}: set";
            var shown = definition.Choices.FirstOrDefault(c => c.Value == p.Value)?.Label ?? p.Value;
            return $"{definition.Label}: {shown}";
        }));

    private static int IndexOf(IReadOnlyList<SettingDefinition> definitions, string key)
    {
        for (int i = 0; i < definitions.Count; i++)
            if (definitions[i].Key == key) return i;
        return int.MaxValue;
    }

    /// <summary>What a group's files share, and what a group is.</summary>
    private static string DescribeGroup(int number, string target, IReadOnlyList<SettingDefinition> definitions, DetailSettings settings) =>
        $"Group {number} → {target}: {DescribeSettings(definitions, settings)}\n\n" +
        "Files of one colour were given the same settings of their own. Click here to select them all and see or change " +
        "their settings together.";

    /// <summary>What every file of a format gets, and where that is set.</summary>
    private static string DescribeFormatSettings(string target, IReadOnlyList<SettingDefinition> definitions, DetailSettings settings) =>
        $"Every file converted to {target}: {DescribeSettings(definitions, settings)}\n\n" +
        $"These are the settings for every file converted to {target}, now and later — they are remembered for next time. " +
        "Click here to see or change them.";

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
            OnPropertyChanged(nameof(ResultLevel));
            OnPropertyChanged(nameof(ResultText));
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
        SetTargets(Items, value);
        // The box gives an order, it does not hold a state: it goes back to its placeholder once
        // the order is carried out, so that the same format can be chosen again later.
        Dispatcher.UIThread.Post(() => BulkTarget = null);
    }

    partial void OnSelectionTargetChanged(TargetChoice? value)
    {
        if (value == null || _replacingSelectionChoices) return;
        // what is selected is what is set: the other files, and the files added later, keep theirs
        SetTargets(_selection.Where(Items.Contains).ToList(), value.Target);
        Dispatcher.UIThread.Post(() => SelectionTarget = null);
    }

    /// <summary>Sets each of these files that can become <paramref name="target"/> to it.</summary>
    private void SetTargets(IReadOnlyList<FileItemViewModel> items, string target)
    {
        _bulkUpdate = true;
        try
        {
            foreach (var item in items)
                if (item.State != ItemState.Converting && item.TargetOptions.Contains(target))
                    item.SelectedTarget = target;
        }
        finally
        {
            _bulkUpdate = false;
        }
        RefreshContextUi();
    }

    /// <summary>
    /// What the box under the list offers for the files selected: every format one of them can
    /// become, in the order their own lists have, a format only some can become with how many.
    /// </summary>
    private void RefreshSelectionTarget()
    {
        var inList = _selection.Count >= 2 ? Items.ToHashSet() : [];
        var selected = _selection.Where(i => i.TargetOptions.Count > 0 && inList.Contains(i)).ToList();
        ShowSelectionTarget = selected.Count >= 2;
        SelectionText = ShowSelectionTarget ? $"{selected.Count} files selected · Set them to" : "";
        var choices = selected.SelectMany(i => i.TargetOptions).Distinct()
            .Select(target => (Target: target, Count: selected.Count(i => i.TargetOptions.Contains(target))))
            .Select(c => new TargetChoice(c.Target, c.Count == selected.Count ? c.Target : $"{c.Target}  ({c.Count} of {selected.Count})"))
            .ToList();
        if (choices.SequenceEqual(SelectionTargetChoices)) return;
        _replacingSelectionChoices = true;
        try
        {
            SelectionTargetChoices = choices;
        }
        finally
        {
            _replacingSelectionChoices = false;
        }
    }

    /// <summary>What <see cref="_replacingBulkChoices"/> is to "Set all to", for the box under the list.</summary>
    private bool _replacingSelectionChoices;

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

        RefreshSelectionTarget();
        RefreshButtons();
        RefreshCustomMarks();
        ShowDetail();
    }

    /// <summary>
    /// "3 files → one PDF (a.png → c.png) · Group 2: 2 files → one PDF (d.png → e.png)": what is
    /// combined — or, while the box is not ticked, what would be.
    /// </summary>
    private void RefreshMergeHint(List<(string Target, List<FileItemViewModel> Items)> plan)
    {
        HasMergeableGroups = plan.Count > 0;
        MergeHint = string.Join("   ·   ", plan.Select(g =>
        {
            var names = NaturalSort.SortPathsByFileName(g.Items.Select(i => i.SourcePath));
            var first = Path.GetFileName(names[0]);
            var last = Path.GetFileName(names[^1]);
            var group = g.Items[0].Group > 0 ? $"Group {g.Items[0].Group}: " : "";
            return $"{group}{g.Items.Count} files → one {g.Target.ToUpperInvariant()}  ({first} → {last})";
        }));
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
        Speed = SpeedInForce,
        UseGpu = UseGpu
    }.WithDetail(EffectiveSettings(item, merging));

    /// <summary>The speed conversions run at: the one chosen for this PC while Auto is on, else the one picked.</summary>
    public SpeedPreset SpeedInForce => AutoSpeed ? _detected.Preset : (SpeedPreset)Math.Clamp(SpeedIndex, 0, 3);

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

        // context-aware plan: files sharing a combinable target, and the same settings, become one output
        var mergeGroups = CombineFiles ? MergePlan(pending) : [];
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

                // converted by itself, even where it would have been combined with files that are done already
                var result = await _service.ConvertAsync(item.SourcePath, output, OptionsFor(item, merging: false), progress, token);

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
                    group.Target, OptionsFor(group.Items[0], merging: true), outputDirectory, progress, token, whenExists, reserved);

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

    /// <summary>
    /// Stops the running batch and waits until the engines it started have ended and what they
    /// had half written is removed — so that closing the window leaves nothing running and nothing
    /// lying about. (Cancelling ends each engine's process at once; the wait is for the tidying up.)
    /// </summary>
    public async Task StopConvertingAsync()
    {
        _cts?.Cancel();
        if (ConvertAllCommand.ExecutionTask is { } batch)
            await Task.WhenAny(batch, Task.Delay(TimeSpan.FromSeconds(15)));
    }

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

    partial void OnIsConvertingChanged(bool value)
    {
        ConvertAllCommand.NotifyCanExecuteChanged();
        RestartForUpdateCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A format the files selected can be set to, named with how many of them can when not all can.</summary>
public sealed record TargetChoice(string Target, string Label)
{
    public override string ToString() => Label;
}
