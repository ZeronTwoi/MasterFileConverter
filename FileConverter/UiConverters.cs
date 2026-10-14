using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using FileConverter.Core;

namespace FileConverter;

/// <summary>
/// The colour of a settings group (1, 2, 3 …): files that convert with the same Format settings
/// share one. Eight colours that read on dark and light alike; a ninth group starts them again,
/// its number telling it apart.
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

/// <summary>The chip colour of a file's category. Chip colours are the same in every theme.</summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            FileCategory.Image => "CatImageBrush",
            FileCategory.Audio => "CatAudioBrush",
            FileCategory.Video => "CatVideoBrush",
            FileCategory.Document => "CatDocumentBrush",
            FileCategory.Archive => "CatArchiveBrush",
            _ => "CatOtherBrush"
        };
        return Application.Current!.TryFindResource(key, out var brush) ? brush : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
