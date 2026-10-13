using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FileConverter.Core;

namespace FileConverter.Views;

/// <summary>
/// A question with two answers, over the window it concerns. The one that changes nothing is the
/// default: Enter, Escape and closing the dialog all mean "no".
/// </summary>
internal sealed class ConfirmDialog : Window
{
    internal ConfirmDialog(string heading, string text, string yes, string no)
    {
        Title = AppInfo.DisplayName;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yesButton = new Button { Content = yes, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        yesButton.Click += (_, _) => Close(true);
        var noButton = new Button
        {
            Content = no,
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsCancel = true
        };
        noButton.Classes.Add("accent");
        noButton.Click += (_, _) => Close(false);

        var explanation = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        explanation.Classes.Add("dim");

        Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Children =
            {
                new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                explanation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 20, 0, 0),
                    Children = { yesButton, noButton }
                }
            }
        };
    }

    /// <summary>Puts the question; true for <paramref name="yes"/>.</summary>
    public static Task<bool> AskAsync(Window owner, string heading, string text, string yes, string no) =>
        new ConfirmDialog(heading, text, yes, no).ShowDialog<bool>(owner);
}
