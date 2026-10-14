using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FileConverter.Core;
using FileConverter.Core.Settings;

namespace FileConverter.ViewModels;

/// <summary>One precise setting as a row of the panel.</summary>
public abstract partial class SettingRowViewModel(SettingDefinition definition) : ObservableObject
{
    public SettingDefinition Definition { get; } = definition;
    public string Label => Definition.Label;
    public string Help => Definition.Help;

    /// <summary>False while the row belongs to a way of setting size and quality that is not the one chosen.</summary>
    [ObservableProperty]
    private bool _isShown = true;

    /// <summary>Called with the tidied value (null = automatic) whenever the user changes the row.</summary>
    public Action<SettingRowViewModel, string?>? Changed { get; set; }

    /// <summary>Shows a value that came from the settings, without reporting it back as a change.</summary>
    public abstract void Show(string? value, string automaticText);
}

/// <summary>A number, a text or a password: a box that is empty while the setting is automatic.</summary>
public partial class TextSettingRow(SettingDefinition definition) : SettingRowViewModel(definition)
{
    private bool _showing;

    [ObservableProperty]
    private string _text = "";

    /// <summary>Shown in the empty box: what happens when nothing is typed.</summary>
    [ObservableProperty]
    private string _watermark = "";

    public bool IsSecret => Definition.IsSecret;

    public override void Show(string? value, string automaticText)
    {
        _showing = true;
        Text = value ?? "";
        Watermark = automaticText;
        _showing = false;
    }

    partial void OnTextChanged(string value)
    {
        if (_showing) return;
        var tidy = Definition.Normalize(value);
        Changed?.Invoke(this, tidy);
        // what was typed is replaced by what it was understood as ("250" → "100", "abc" → empty)
        if ((tidy ?? "") != value)
        {
            _showing = true;
            Text = tidy ?? "";
            _showing = false;
        }
    }
}

/// <summary>One of a few named values, the first of which is "automatic" — or, for a choice that is always made, the first choice.</summary>
public partial class ChoiceSettingRow : SettingRowViewModel
{
    private bool _showing;

    public ObservableCollection<SettingChoice> Options { get; } = [];

    [ObservableProperty]
    private SettingChoice? _selected;

    public ChoiceSettingRow(SettingDefinition definition) : base(definition)
    {
        if (!definition.ChoiceRequired) Options.Add(new SettingChoice("", definition.Automatic));
        foreach (var choice in definition.Choices) Options.Add(choice);
    }

    public override void Show(string? value, string automaticText)
    {
        _showing = true;
        if (!Definition.ChoiceRequired) Options[0] = new SettingChoice("", automaticText);
        // a choice that is always made shows none while the files shown have different ones
        Selected = Options.FirstOrDefault(o => o.Value == (value ?? "")) ??
                   (Definition.ChoiceRequired && value == null ? null : Options[0]);
        _showing = false;
    }

    partial void OnSelectedChanged(SettingChoice? value)
    {
        if (_showing || value == null) return;
        Changed?.Invoke(this, value.Value.Length == 0 ? null : value.Value);
    }
}

public sealed record SettingGroupViewModel(string Name, IReadOnlyList<SettingRowViewModel> Rows);

/// <summary>A group the selected files can be put in — or, as number 0, none: no settings of their own.</summary>
public sealed record GroupChoice(int Number, string Label)
{
    public bool IsGroup => Number > 0;
}

/// <summary>A preset in the panel's list; the user's own can be deleted.</summary>
public sealed record PresetChoice(SettingsPreset Preset, bool IsBuiltIn)
{
    public string Name => Preset.Name;
}

/// <summary>
/// The Format settings panel: the precise settings of the selected files' conversion. They are
/// set for the files selected — one, or several converted the same way, which then share them
/// and are shown as a group — or, when chosen, for every file converted to the format, now and
/// later ("everything I turn into MP4"), which is remembered. Whatever is left empty stays
/// automatic. What the bar at the bottom sets for all files — speed, where results go — is not
/// here; size and quality are set in one of the two places, and the panel's "Set by" choice
/// says which.
/// </summary>
public partial class DetailPanelViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    /// <summary>The files the panel is for; their settings of their own are set all alike.</summary>
    private IReadOnlyList<FileItemViewModel> _items = [];
    /// <summary>The first of them, which stands for all in what is the same for each (the target, the settings offered).</summary>
    private FileItemViewModel? _item;
    private IReadOnlyList<SettingDefinition> _definitions = [];
    private bool _rebuilding;

    public ObservableCollection<SettingGroupViewModel> Groups { get; } = [];
    public ObservableCollection<PresetChoice> Presets { get; } = [];

    /// <summary>A file with a target is selected.</summary>
    [ObservableProperty]
    private bool _hasContext;

    /// <summary>The selected conversion has anything to set.</summary>
    [ObservableProperty]
    private bool _hasSettings;

    [ObservableProperty]
    private string _title = "Format settings";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _emptyText = "Select a file to see the settings for its conversion.";

    /// <summary>True: the settings are for the files selected alone. False: for every file with this target.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGroupChoice))]
    private bool _onlyThisFile = true;

    [ObservableProperty]
    private string _scopeAllText = "Every file of this format";

    [ObservableProperty]
    private string _scopeFileText = "Only this file";

    /// <summary>The groups the files selected can be put in, and "none"; offered while there is a group to choose.</summary>
    public ObservableCollection<GroupChoice> GroupChoices { get; } = [];

    /// <summary>The group the files selected are in; null while they are in different ones.</summary>
    [ObservableProperty]
    private GroupChoice? _selectedGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGroupChoice))]
    private bool _hasGroupChoices;

    public bool ShowGroupChoice => OnlyThisFile && HasGroupChoices;

    /// <summary>Said while the files are combined into one result with others.</summary>
    [ObservableProperty]
    private string? _mergeNote;

    /// <summary>Set when the settings mean a stream is encoded again instead of copied.</summary>
    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private PresetChoice? _selectedPreset;

    [ObservableProperty]
    private bool _hasPresets;

    [ObservableProperty]
    private string _newPresetName = "";

    public DetailPanelViewModel(MainViewModel owner) => _owner = owner;

    public bool ScopeIsAll
    {
        get => !OnlyThisFile;
        set { if (value) OnlyThisFile = false; }
    }

    partial void OnOnlyThisFileChanged(bool value)
    {
        OnPropertyChanged(nameof(ScopeIsAll));
        if (!_rebuilding) ShowValues();
    }

    /// <summary>Rebuilds the panel for the files selected (or for none).</summary>
    public void Show(IReadOnlyList<FileItemViewModel> items)
    {
        _rebuilding = true;
        try
        {
            _items = items;
            _item = items.Count > 0 ? items[0] : null;
            var item = _item;
            Groups.Clear();
            Presets.Clear();
            SelectedPreset = null;
            Warning = null;
            GroupChoices.Clear();
            SelectedGroup = null;
            HasGroupChoices = false;
            MergeNote = null;

            if (item?.SelectedTarget is not { } target)
            {
                HasContext = false;
                HasSettings = false;
                Title = "Format settings";
                Subtitle = "";
                EmptyText = _owner.Items.Count == 0
                    ? "Add a file, then select it to see the settings its target format has."
                    : "Select a file to see the settings its target format has.";
                return;
            }

            if (items.Count > 1 && !_owner.ConvertTheSameWay(items))
            {
                // settings are set together only where they are the same settings
                _items = [];
                _item = null;
                HasContext = true;
                HasSettings = false;
                Title = "Format settings";
                Subtitle = $"{items.Count} files selected";
                var ways = items.Select(i => $"{i.Extension.ToUpperInvariant()} → {i.SelectedTarget?.ToUpperInvariant() ?? "nothing"}")
                    .Distinct().Take(4);
                EmptyText = $"These files are converted in different ways ({string.Join(", ", ways)}). Select files that become " +
                            "the same format in the same way — with Ctrl or Shift — to set them together.";
                return;
            }

            HasContext = true;
            Title = $"{target.ToUpperInvariant()} settings";
            Subtitle = items.Count == 1 ? $"{item.FileName}  →  {target}" : $"{items.Count} files  →  {target}";
            ScopeAllText = $"Every file converted to {target.ToUpperInvariant()}, now and later";
            ScopeFileText = items.Count == 1 ? "Only this file" : $"Only these {items.Count} files";
            // what is selected is what is set — every file of the format only when asked for
            OnlyThisFile = true;

            bool merged = _owner.IsMerged(item);
            _definitions = SettingCatalog.For(item.Extension, target, merged);
            if (merged)
                MergeNote = $"Combined into one {target.ToUpperInvariant()} with the other files that have the same settings. " +
                            $"Files given other settings here become a {target.ToUpperInvariant()} of their own.";
            HasSettings = _definitions.Any(d => d.InPanel);
            EmptyText = "This conversion has nothing to set here — it only has one way of being done.";

            foreach (var group in _definitions.Where(d => d.InPanel).GroupBy(d => d.Group))
            {
                var rows = group.Select(Row).ToList();
                Groups.Add(new SettingGroupViewModel(group.Key, rows));
            }

            var kind = SettingCatalog.KindOf(target);
            var keys = _definitions.Select(d => d.Key).ToHashSet();
            foreach (var preset in SettingCatalog.BuiltInPresets.Where(p => p.Kind == kind && p.Values.Keys.Any(keys.Contains)))
                Presets.Add(new PresetChoice(preset, IsBuiltIn: true));
            foreach (var preset in _owner.Settings.Presets.Where(p => p.Kind == kind && p.Values.Keys.Any(keys.Contains)))
                Presets.Add(new PresetChoice(preset, IsBuiltIn: false));
            HasPresets = Presets.Count > 0;
        }
        finally
        {
            _rebuilding = false;
        }
        ShowValues();
    }

    private SettingRowViewModel Row(SettingDefinition definition)
    {
        SettingRowViewModel row = definition.Kind == SettingKind.Choice
            ? new ChoiceSettingRow(definition)
            : new TextSettingRow(definition);
        row.Changed = OnRowChanged;
        return row;
    }

    /// <summary>Fills every row from the scope being edited.</summary>
    private void ShowValues()
    {
        if (_item?.SelectedTarget is not { } target) return;
        var format = _owner.FormatValues(target);
        // the way size and quality are set: a file without one of its own follows its format's —
        // and files selected together that have different ones show none until one is picked for all
        var modes = OnlyThisFile
            ? _items.Select(i => SettingCatalog.ModeOf(_definitions, DetailSettings.Merge(format, i.Overrides).Values)).Distinct().ToList()
            : [SettingCatalog.ModeOf(_definitions, format)];
        string? mode = modes.Count == 1 ? modes[0] : null;
        foreach (var row in Groups.SelectMany(g => g.Rows))
        {
            var key = row.Definition.Key;
            row.IsShown = row.Definition.Mode == null || row.Definition.Mode == mode;
            if (key == SettingKeys.SizeMode)
            {
                row.Show(mode, "");
            }
            else if (OnlyThisFile)
            {
                // an empty box means "as for all files of this format", and says what that is —
                // or, for several files whose own values differ, that they do
                var owns = _items.Select(i => i.Overrides.GetValueOrDefault(key)).Distinct().ToList();
                string? own = owns.Count == 1 ? owns[0] : null;
                string inherited = owns.Count > 1
                    ? $"Not the same in all {_items.Count}"
                    : format.TryGetValue(key, out var shared)
                        ? row.Definition.IsSecret ? "As for all files" : $"{Display(row.Definition, shared)} (as for all {target.ToUpperInvariant()})"
                        : row.Definition.Automatic;
                row.Show(own, inherited);
            }
            else
            {
                format.TryGetValue(key, out var value);
                row.Show(value, row.Definition.Automatic);
            }
        }
        RefreshWarning();
        RefreshGroupChoices();
    }

    /// <summary>
    /// Offers the groups of files converted the same way as the ones selected, so they can be
    /// put in one — given the same settings — or taken out of theirs.
    /// </summary>
    private void RefreshGroupChoices()
    {
        bool was = _rebuilding;
        _rebuilding = true;
        try
        {
            GroupChoices.Clear();
            if (_item?.SelectedTarget is not { } target) return;
            var groups = _owner.GroupsLike(_item);
            if (groups.Count == 0) return;
            GroupChoices.Add(new GroupChoice(0, $"None — as every {target.ToUpperInvariant()}"));
            foreach (var (number, count) in groups)
                GroupChoices.Add(new GroupChoice(number, count == 1 ? $"Group {number} · 1 file" : $"Group {number} · {count} files"));
            var theirs = _items.Select(i => i.Group).Distinct().ToList();
            SelectedGroup = theirs.Count == 1 ? GroupChoices.FirstOrDefault(c => c.Number == theirs[0]) : null;
        }
        finally
        {
            HasGroupChoices = GroupChoices.Count > 0;
            _rebuilding = was;
        }
    }

    partial void OnSelectedGroupChanged(GroupChoice? value)
    {
        if (_rebuilding || value == null || _item?.SelectedTarget == null) return;
        _owner.PutInGroup(_items, value.Number);
        _rebuilding = true;
        SelectedPreset = null;
        _rebuilding = false;
        ShowValues();
    }

    private static string Display(SettingDefinition definition, string value) =>
        definition.Choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    /// <summary>Sets (or, with null, clears) one setting in the scope being edited — for each of the files, if it is theirs.</summary>
    private void Store(string target, SettingDefinition definition, string? value)
    {
        if (OnlyThisFile)
        {
            foreach (var item in _items) Store(item, definition, value);
        }
        else
        {
            _owner.SetFormatValue(target, definition, value);
        }
    }

    private static void Store(FileItemViewModel item, SettingDefinition definition, string? value)
    {
        if (value == null) item.Overrides.Remove(definition.Key);
        else item.Overrides[definition.Key] = value;
    }

    /// <summary>
    /// After a change to the files' own settings: a file's own way of setting size and quality is
    /// kept where it says something — a way unlike its format's, or the way its own values belong
    /// to, which then stay in force should the format's way change — and dropped where it only
    /// repeats its format's, so that nothing of their own is left where they have nothing of
    /// their own.
    /// </summary>
    private void TidyModes(string target)
    {
        if (!OnlyThisFile) return;
        var formatMode = SettingCatalog.ModeOf(_definitions, _owner.FormatValues(target));
        foreach (var own in _items.Select(i => i.Overrides))
        {
            if (own.TryGetValue(SettingKeys.SizeMode, out var mode))
            {
                if (mode == formatMode && !_definitions.Any(d => d.Mode == mode && own.ContainsKey(d.Key)))
                    own.Remove(SettingKeys.SizeMode);
            }
            else if (_definitions.FirstOrDefault(d => d.Mode != null && own.ContainsKey(d.Key)) is { Mode: { } valuesMode })
            {
                own[SettingKeys.SizeMode] = valuesMode;
            }
        }
    }

    private void OnRowChanged(SettingRowViewModel row, string? value)
    {
        if (_rebuilding || _item?.SelectedTarget is not { } target) return;

        if (row.Definition.Key == SettingKeys.SizeMode) ChooseMode(target, row.Definition, value ?? SizeModes.Slider);
        else Store(target, row.Definition, value);
        TidyModes(target);

        // a setting changed by hand is no longer "the preset"
        _rebuilding = true;
        SelectedPreset = null;
        _rebuilding = false;

        _owner.OnDetailSettingsChanged();
        if (row.Definition.Key == SettingKeys.SizeMode) ShowValues();
        else
        {
            RefreshWarning();
            RefreshGroupChoices();
        }
    }

    /// <summary>
    /// Switches the way size and quality are set, in the scope being edited. The settings of the
    /// other ways are cleared, so none is left over to compete with the one chosen, and the
    /// chosen one's setting gets a starting value when it has none yet.
    /// </summary>
    private void ChooseMode(string target, SettingDefinition choice, string mode)
    {
        var format = _owner.FormatValues(target);
        // for every file of a format no choice means the slider; for the files selected it means
        // their format's way (see TidyModes)
        Store(target, choice, OnlyThisFile || mode != SizeModes.Slider ? mode : null);
        foreach (var definition in _definitions.Where(d => d.Mode != null))
        {
            if (definition.Mode != mode)
            {
                Store(target, definition, null);
            }
            else if (definition.Suggested != null && !format.ContainsKey(definition.Key))
            {
                if (!OnlyThisFile) Store(target, definition, definition.Suggested);
                else foreach (var item in _items.Where(i => !i.Overrides.ContainsKey(definition.Key)))
                    Store(item, definition, definition.Suggested);
            }
        }
    }

    /// <summary>Says so when the settings in force mean the video or audio is encoded again.</summary>
    private void RefreshWarning()
    {
        if (_item?.SelectedTarget is not { } target) { Warning = null; return; }

        var effective = _owner.EffectiveSettings(_item);
        var forcing = _definitions.Where(d => d.Reencodes && effective.Has(d.Key)).Select(d => d.Label).ToList();
        if (forcing.Count == 0) { Warning = null; return; }

        string what = SettingCatalog.KindOf(target) == "video" ? "video" : "audio";
        Warning = $"With {string.Join(", ", forcing.Take(3))}{(forcing.Count > 3 ? " …" : "")} set, the {what} is encoded again " +
                  "instead of being copied. 100% file size then means \"as good as it gets\", not a bit-for-bit copy.";
    }

    partial void OnSelectedPresetChanged(PresetChoice? value)
    {
        OnPropertyChanged(nameof(CanDeletePreset));
        DeletePresetCommand.NotifyCanExecuteChanged();
        if (_rebuilding || value == null || _item?.SelectedTarget is not { } target) return;

        // a preset is a complete state: what it doesn't mention goes back to automatic
        foreach (var definition in _definitions.Where(d => !d.IsSecret))
        {
            string? tidy = value.Preset.Values.TryGetValue(definition.Key, out var raw) ? definition.Normalize(raw) : null;
            Store(target, definition, tidy);
        }
        // …and the way of setting size and quality is the one its values belong to
        var mode = SettingCatalog.ModeOf(_definitions, value.Preset.Values);
        if ((OnlyThisFile || mode != SizeModes.Slider) && _definitions.FirstOrDefault(d => d.Key == SettingKeys.SizeMode) is { } choice)
            Store(target, choice, mode);
        TidyModes(target);
        _owner.OnDetailSettingsChanged();
        ShowValues();
    }

    /// <summary>Everything in the scope being edited back to automatic.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (_item?.SelectedTarget is not { } target) return;
        foreach (var definition in _definitions) Store(target, definition, null);
        _rebuilding = true;
        SelectedPreset = null;
        _rebuilding = false;
        _owner.OnDetailSettingsChanged();
        ShowValues();
    }

    private bool CanSavePreset() => !string.IsNullOrWhiteSpace(NewPresetName) && _item?.SelectedTarget != null;

    partial void OnNewPresetNameChanged(string value) => SavePresetCommand.NotifyCanExecuteChanged();

    /// <summary>Saves what is in force for the selected file as a named preset (passwords are never included).</summary>
    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        if (_item?.SelectedTarget is not { } target) return;
        var effective = _owner.EffectiveSettings(_item);
        var values = _definitions.Where(d => !d.IsSecret && effective.Has(d.Key))
            .ToDictionary(d => d.Key, d => effective.Text(d.Key)!);
        if (values.Count == 0) return;

        var kind = SettingCatalog.KindOf(target);
        var name = NewPresetName.Trim();
        _owner.Settings.Presets.RemoveAll(p => p.Kind == kind && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var preset = new SettingsPreset { Kind = kind, Name = name, Values = values };
        _owner.Settings.Presets.Add(preset);
        _owner.Persist();

        NewPresetName = "";
        foreach (var existing in Presets.Where(p => !p.IsBuiltIn && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList())
            Presets.Remove(existing);
        var choice = new PresetChoice(preset, IsBuiltIn: false);
        Presets.Add(choice);
        HasPresets = true;
        _rebuilding = true;
        SelectedPreset = choice;
        _rebuilding = false;
        OnPropertyChanged(nameof(CanDeletePreset));
        DeletePresetCommand.NotifyCanExecuteChanged();
    }

    public bool CanDeletePreset => SelectedPreset is { IsBuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is not { IsBuiltIn: false } choice) return;
        _owner.Settings.Presets.Remove(choice.Preset);
        _owner.Persist();
        _rebuilding = true;
        SelectedPreset = null;
        _rebuilding = false;
        Presets.Remove(choice);
        HasPresets = Presets.Count > 0;
    }
}
