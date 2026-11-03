using System.Globalization;
using Avalonia.Data.Converters;

namespace FileConverter;

/// <summary>
/// The colour of a settings group (1, 2, 3 …): files given the same settings of their own share
/// one. Eight colours that read on the dark background; a ninth group starts them again, its
/// number telling it apart.
/// </summary>
public sealed class GroupToBrushConverter : IValueConverter
{
    private static readonly Avalonia.Media.IBrush[] Palette =
        new[] { "#2EC4B6", "#F28C38", "#9B6BFF", "#F25F9B", "#7FB800", "#3FA7F5", "#D4A20B", "#E5595F" }
            .Select(hex => (Avalonia.Media.IBrush)new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex)).ToImmutable())
            .ToArray();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int group and > 0 ? Palette[(group - 1) % Palette.Length] : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Is the value the one named by the parameter? (Which colours the menu marks, which name the slider lights.)</summary>
public sealed class IsEqualConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Text in small letters, the way a panel's title and the big button are written.</summary>
public sealed class LowerCaseConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToLowerInvariant();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Progress from 0 to 100 drawn in text, as package managers and btop do: "██████░░░░  60%".
/// The parameter is the number of cells; ten when not given.
/// </summary>
public sealed class ProgressTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Draw(value is double progress ? progress : 0, int.TryParse(parameter?.ToString(), out var cells) ? cells : 10);

    public static string Draw(double progress, int cells)
    {
        double share = double.IsNaN(progress) ? 0 : Math.Clamp(progress, 0, 100) / 100;
        int full = (int)Math.Round(share * cells);
        return new string('█', full) + new string('░', cells - full) +
               (share * 100).ToString("0", CultureInfo.InvariantCulture).PadLeft(4) + "%";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
