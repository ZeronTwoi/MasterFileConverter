using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using FileConverter.Core;

namespace FileConverter;

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
