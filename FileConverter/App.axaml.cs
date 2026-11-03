using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FileConverter.Core;
using FileConverter.Core.Platform;
using FileConverter.Core.Settings;
using FileConverter.ViewModels;
using FileConverter.Views;

namespace FileConverter;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            ApplyLineColour(viewModel.Settings.LineColour);

            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // An error nothing else caught must not close the window on a running batch: it is
            // written to error.log next to the settings and shown in the status line.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                viewModel.OverallStatus = "Something went wrong: " + e.Exception.Message;
                try
                {
                    var log = Path.Combine(PlatformServices.Current.ConfigDirectory, "error.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                    if (File.Exists(log) && new FileInfo(log).Length > 512 * 1024) File.Delete(log);
                    File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {e.Exception}{Environment.NewLine}{Environment.NewLine}");
                }
                catch
                {
                    // the status line has said it; a log that can't be written changes nothing
                }
            };

            // files handed over by "Open with", a drop on the icon, or a desktop entry's %F
            var files = (desktop.Args ?? []).Where(a => !a.StartsWith("--")).ToList();
            if (files.Count > 0) _ = viewModel.AddPathsAsync(files);

            // new versions: the question the first time; afterwards one look per start, if wanted
            viewModel.BeginUpdateNotices();
            // just updated — or the last update did not go through, and why
            var (updatedTo, failure) = AppUpdater.TakeOutcome();
            viewModel.AfterStart(updatedTo, failure);
            _ = TidyUpAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// A little after the start, away from the window's thread: removes what an update left in the
    /// data folder, and work files of a run that ended without tidying up (a crash, a power cut).
    /// </summary>
    private static async Task TidyUpAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            AppUpdater.RemoveLeftovers();
            WorkFiles.RemoveLeftovers(TimeSpan.FromDays(3));
        }
        catch
        {
            // tidying up is a courtesy; the next start tries again
        }
    }

    /// <summary>
    /// Colours the window's lines and accents. The brushes are changed in place, so everything that
    /// uses them — the Fluent controls' borrowed ones too — follows at once.
    /// </summary>
    public void ApplyLineColour(string id)
    {
        var colour = Color.Parse(LineColours.Hex(id));
        Color Darker(double share) => Color.FromRgb((byte)(colour.R * share), (byte)(colour.G * share), (byte)(colour.B * share));
        Color Faint(byte alpha) => Color.FromArgb(alpha, colour.R, colour.G, colour.B);

        Set("LineBrush", colour);
        Set("LineDimBrush", Faint(0x66));
        Set("AccentBrush", colour);
        Set("AccentHoverBrush", Darker(0.85));
        Set("AccentPressedBrush", Darker(0.7));
        Set("SelectionBrush", Faint(0x26));

        // what is left to the Fluent theme's own accent: the focus of a menu's or a list's mark
        foreach (var style in Styles.OfType<FluentTheme>())
            if (style.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
                palette.Accent = colour;
    }

    private void Set(string key, Color colour)
    {
        if (Resources.TryGetResource(key, ThemeVariant.Dark, out var found) && found is SolidColorBrush brush)
            brush.Color = colour;
    }
}
