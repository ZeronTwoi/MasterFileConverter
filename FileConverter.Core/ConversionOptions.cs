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
