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

/// <summary>One of a few named values, the first of which is "automatic".</summary>
public partial class ChoiceSettingRow : SettingRowViewModel
{
    private bool _showing;

    public ObservableCollection<SettingChoice> Options { get; } = [];

    [ObservableProperty]
    private SettingChoice? _selected;

    public ChoiceSettingRow(SettingDefinition definition) : base(definition)
    {
        Options.Add(new SettingChoice("", definition.Automatic));
        foreach (var choice in definition.Choices) Options.Add(choice);
    }

    public override void Show(string? value, string automaticText)
    {
        _showing = true;
        Options[0] = new SettingChoice("", automaticText);
        Selected = Options.FirstOrDefault(o => o.Value == (value ?? "")) ?? Options[0];
        _showing = false;
    }

    partial void OnSelectedChanged(SettingChoice? value)
    {
        if (_showing || value == null) return;
        Changed?.Invoke(this, value.Value.Length == 0 ? null : value.Value);
    }
}

public sealed record SettingGroupViewModel(string Name, IReadOnlyList<SettingRowViewModel> Rows);

/// <summary>A preset in the panel's list; the user's own can be deleted.</summary>
public sealed record PresetChoice(SettingsPreset Preset, bool IsBuiltIn)
{
    public string Name => Preset.Name;
}

/// <summary>
/// The Detailed Settings panel: the precise settings of the selected file's conversion.
/// They are kept per target format ("everything I turn into MP4") and can be overridden for
/// one file. Whatever is left empty stays automatic.
/// </summary>
public partial class DetailPanelViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
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
    private string _title = "Detailed settings";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _emptyText = "Select a file to see the settings for its conversion.";

    /// <summary>False: the settings are for every file with this target. True: for the selected file alone.</summary>
    [ObservableProperty]
    private bool _onlyThisFile;

    [ObservableProperty]
    private string _scopeAllText = "All files";

    [ObservableProperty]
    private string _scopeFileText = "Only this file";

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

    /// <summary>Rebuilds the panel for a file (or for none).</summary>
    public void Show(FileItemViewModel? item)
    {
        _rebuilding = true;
        try
        {
            _item = item;
            Groups.Clear();
            Presets.Clear();
            SelectedPreset = null;
            Warning = null;

            if (item?.SelectedTarget is not { } target)
            {
                HasContext = false;
                HasSettings = false;
                Title = "Detailed settings";
                Subtitle = "";
                EmptyText = _owner.Items.Count == 0
                    ? "Add a file, then select it to set its conversion precisely."
                    : "Select a file to see the settings for its conversion.";
                return;
            }

            HasContext = true;
            Title = $"{target.ToUpperInvariant()} settings";
            Subtitle = $"{item.FileName}  →  {target}";
            ScopeAllText = $"All files → {target.ToUpperInvariant()}";
            ScopeFileText = "Only this file";
            // a file that already has settings of its own opens on them
            OnlyThisFile = item.Overrides.Count > 0;

            _definitions = SettingCatalog.For(item.Extension, target, _owner.IsMerged(item));
            HasSettings = _definitions.Count > 0;
            EmptyText = "This conversion has nothing to set — it only has one way of being done.";

            foreach (var group in _definitions.GroupBy(d => d.Group))
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
        foreach (var row in Groups.SelectMany(g => g.Rows))
        {
            var key = row.Definition.Key;
            if (OnlyThisFile)
            {
                // an empty box means "as for all files of this format", and says what that is
                _item.Overrides.TryGetValue(key, out var own);
                string inherited = format.TryGetValue(key, out var shared)
                    ? $"{Display(row.Definition, shared)} (as for all {target.ToUpperInvariant()})"
                    : row.Definition.Automatic;
                row.Show(own, row.Definition.IsSecret && shared != null ? "As for all files" : inherited);
            }
            else
            {
                format.TryGetValue(key, out var value);
                row.Show(value, row.Definition.Automatic);
            }
        }
        RefreshWarning();
    }

    private static string Display(SettingDefinition definition, string value) =>
        definition.Choices.FirstOrDefault(c => c.Value == value)?.Label ?? value;

    private void OnRowChanged(SettingRowViewModel row, string? value)
    {
        if (_rebuilding || _item?.SelectedTarget is not { } target) return;

        if (OnlyThisFile)
        {
            if (value == null) _item.Overrides.Remove(row.Definition.Key);
            else _item.Overrides[row.Definition.Key] = value;
        }
        else
        {
            _owner.SetFormatValue(target, row.Definition, value);
        }

        // a setting changed by hand is no longer "the preset"
        _rebuilding = true;
        SelectedPreset = null;
        _rebuilding = false;

        _owner.OnDetailSettingsChanged();
        RefreshWarning();
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
            if (OnlyThisFile)
            {
                if (tidy == null) _item.Overrides.Remove(definition.Key);
                else _item.Overrides[definition.Key] = tidy;
            }
            else
            {
                _owner.SetFormatValue(target, definition, tidy);
            }
        }
        _owner.OnDetailSettingsChanged();
        ShowValues();
    }

    /// <summary>Everything in the scope being edited back to automatic.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (_item?.SelectedTarget is not { } target) return;
        foreach (var definition in _definitions)
        {
            if (OnlyThisFile) _item.Overrides.Remove(definition.Key);
            else _owner.SetFormatValue(target, definition, null);
        }
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
