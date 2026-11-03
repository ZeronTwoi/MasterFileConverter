using FileConverter.Core.Settings;

namespace FileConverter.Core;

public enum SpeedPreset
{
    Fastest,
    Fast,
    Balanced,
    BestQuality
}

public sealed record ConversionOptions
{
    /// <summary>Target output size as a percentage of the source file size. 100 = best quality / no size target.</summary>
    public int SizePercent { get; init; } = 100;

    public SpeedPreset Speed { get; init; } = SpeedPreset.Balanced;

    /// <summary>Let video encodes run on the GPU's hardware encoder when one works on this machine.</summary>
    public bool UseGpu { get; init; } = true;

    public bool HasSizeTarget => SizePercent < 100;

    /// <summary>
    /// Precise settings from the Format settings panel. Empty by default, and then every
    /// conversion decides for itself exactly as it does without the panel.
    /// </summary>
    public DetailSettings Detail { get; init; } = DetailSettings.None;

    /// <summary>These options with a file's precise settings applied (among them may be its own file size and speed).</summary>
    public ConversionOptions WithDetail(DetailSettings detail) => this with
    {
        Detail = detail,
        SizePercent = detail.Int(SettingKeys.SizePercent, 10, 100) ?? SizePercent,
        Speed = detail.Text(SettingKeys.Speed) switch
        {
            "fastest" => SpeedPreset.Fastest,
            "fast" => SpeedPreset.Fast,
            "balanced" => SpeedPreset.Balanced,
            "best" => SpeedPreset.BestQuality,
            _ => Speed
        }
    };
}

/// <summary>One position of the Result slider: how small the result is made and how fast it is encoded.</summary>
/// <param name="Name">What the command line and agents call it.</param>
/// <param name="SizePercent">The target size, as a share of the source; 100 keeps the best quality the format allows.</param>
/// <param name="Speed">How much time the encoders take over it.</param>
/// <param name="Text">What it means, said under the slider.</param>
public sealed record ResultLevel(string Name, int SizePercent, SpeedPreset Speed, string Text);

/// <summary>
/// The Result slider, the one control for how far a result is taken: from small and quick to
/// everything kept and the most care. The small end is Fast, not Fastest: the fastest encoders
/// miss a size target by more and look worst exactly where few bits are left.
/// </summary>
public static class ResultLevels
{
    public static IReadOnlyList<ResultLevel> All { get; } =
    [
        new("smallest", 25, SpeedPreset.Fast, "About 25% of the size, quick"),
        new("small", 50, SpeedPreset.Fast, "About 50% of the size, quick"),
        new("good", 75, SpeedPreset.Balanced, "About 75% of the size"),
        new("full", 100, SpeedPreset.Balanced, "Full quality"),
        new("best", 100, SpeedPreset.BestQuality, "Full quality, smallest, slowest")
    ];

    /// <summary>Where the slider starts: nothing lost, in reasonable time.</summary>
    public const int Default = 3;

    /// <summary>The level of this name or position, or null.</summary>
    public static ResultLevel? Find(string nameOrPosition) =>
        int.TryParse(nameOrPosition, out var position)
            ? position >= 0 && position < All.Count ? All[position] : null
            : All.FirstOrDefault(level => level.Name.Equals(nameOrPosition, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The position that comes closest to a size and speed, which is how settings are kept
    /// (and how versions before 2.8, with a slider for each, kept them).
    /// </summary>
    public static int Nearest(int sizePercent, SpeedPreset speed) => sizePercent switch
    {
        < 38 => 0,
        < 63 => 1,
        < 100 => 2,
        _ => speed == SpeedPreset.BestQuality ? 4 : 3
    };
}

/// <summary>
/// What a conversion produced: the output path (a folder when the conversion fans out into
/// several files) and an optional short remark for the UI, e.g. "lossless copy".
/// </summary>
public sealed record ConversionResult(string OutputPath, string? Note = null);

/// <summary>Thrown for errors the user can act on; shown verbatim in the UI.</summary>
public sealed class FriendlyException(string message) : Exception(message);

public static class PerformanceDetector
{
    /// <summary>
    /// Picks a speed preset for this machine. With a working GPU encoder "Balanced" is already
    /// fast; on the CPU alone, fewer cores get a lighter encoder preset so wall-clock time stays
    /// reasonable. The slowest preset is never chosen automatically — at 100 % it only buys
    /// smaller files, not better quality.
    /// Uses only local hardware counters — nothing leaves the machine.
    /// </summary>
    public static (SpeedPreset Preset, string Description) Detect(GpuEncoder gpu = GpuEncoder.None)
    {
        int cores = Environment.ProcessorCount;
        double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;

        SpeedPreset preset;
        if (gpu != GpuEncoder.None || cores >= 12) preset = SpeedPreset.Balanced;
        else if (cores >= 6) preset = SpeedPreset.Fast;
        else preset = SpeedPreset.Fastest;

        var description = $"{cores} cores, {ramGb:0} GB RAM";
        if (gpu != GpuEncoder.None) description += $", {HardwareEncoders.Describe(gpu)}";
        return (preset, description);
    }
}
