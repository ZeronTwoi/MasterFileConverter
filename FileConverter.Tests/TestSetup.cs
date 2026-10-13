using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using FileConverter.Core;
using FileConverter.Platform;
using FileConverter.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace FileConverter.Tests;

internal static class TestSetup
{
    /// <summary>The engines need their platform before any test touches them.</summary>
    [ModuleInitializer]
    public static void Initialize() => Engines.Initialize(HostPlatform.Create());

    /// <summary>A scratch folder for one test, removed again when disposed.</summary>
    public static TempFolder NewFolder([CallerMemberName] string name = "test") => new(name);
}

/// <summary>The real app, drawn by the real renderer, but into memory instead of a window.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHarfBuzz()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TempFolder : IDisposable
{
    public string Path { get; }

    public TempFolder(string name)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fc-test-{name}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* a scratch folder left behind is harmless */ }
    }
}
